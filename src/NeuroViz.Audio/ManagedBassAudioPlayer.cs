using System.Runtime.InteropServices;
using ManagedBass;
using BassState = ManagedBass.PlaybackState;
using NeuroViz.Core.Ipc;

namespace NeuroViz.Audio;

public sealed class ManagedBassAudioPlayer : IAudioPlayer
{
    private readonly AudioClockWriter? _clock;
    private int _handle;
    private string? _loadedPath;
    private long _seq;
    private long _dspSampleCursor;   // сэмплов на канал, прошедших DSP
    private int _seekGeneration;    // ← новое: инвалидирует in-flight кадры при seek/stop/load
    private DSPProcedure? _dsp;
    private bool _disposed;

    public ManagedBassAudioPlayer(AudioClockWriter? clock = null)
    {
        _clock = clock;
        BassBootstrap.EnsureInitialized();
    }

    public event EventHandler<PcmFrame>? PcmAvailable;

    // Position теперь — позиция головки DSP, а не «что слышит пользователь».
    // Для анализа и таймлайна это ровно то, что нужно.
    public double Position => (double)Interlocked.Read(ref _dspSampleCursor) / SampleRate;

    public double Duration
    {
        get
        {
            if (_handle == 0) return 0;
            var bytes = Bass.ChannelGetLength(_handle);
            return Bass.ChannelBytes2Seconds(_handle, bytes);
        }
    }

    public bool IsPlaying => _handle != 0 && Bass.ChannelIsActive(_handle) == BassState.Playing;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public void Load(string path)
    {
        ThrowIfDisposed();
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found", path);

        if (_handle != 0)
        {
            Bass.ChannelStop(_handle);
            Bass.StreamFree(_handle);
            _handle = 0;
        }

        var flags = BassFlags.Float;
        _handle = Bass.CreateStream(path, 0, 0, flags);
        if (_handle == 0)
            throw new InvalidOperationException($"Bass.CreateStream failed: {Bass.LastError}");

        _loadedPath = path;

        var info = Bass.ChannelGetInfo(_handle);
        SampleRate = info.Frequency;
        Channels = info.Channels;

        _seq = 0;
        // Инвалидирует кадры от старого handle, если они ещё в полёте.
        Interlocked.Increment(ref _seekGeneration);
        Interlocked.Exchange(ref _dspSampleCursor, 0);

        _dsp = OnDsp;
        Bass.ChannelSetDSP(_handle, _dsp, IntPtr.Zero, 0);
    }

    public void Play()
    {
        ThrowIfDisposed();
        if (_handle == 0) return;
        Bass.ChannelPlay(_handle);
        _clock?.Update(Position, ClockPlaybackState.Play);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        if (_handle == 0) return;
        Bass.ChannelPause(_handle);
        _clock?.Update(Position, ClockPlaybackState.Pause);
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (_handle == 0) return;

        Interlocked.Increment(ref _seekGeneration);
        Bass.ChannelStop(_handle);
        Bass.ChannelSetPosition(_handle, 0);
        Interlocked.Exchange(ref _dspSampleCursor, 0);
        _clock?.Update(0, ClockPlaybackState.Stop);
    }

    public void Seek(double seconds)
    {
        ThrowIfDisposed();
        if (_handle == 0) return;

        // 1) Сначала сигналим «идёт перемотка» — OnDsp увидит это на своей
        //    контрольной точке и отбросит текущий кадр.
        Interlocked.Increment(ref _seekGeneration);

        // 2) Меняем позицию BASS и наш курсор.
        var bytes = Bass.ChannelSeconds2Bytes(_handle, seconds);
        Bass.ChannelSetPosition(_handle, bytes);
        Interlocked.Exchange(ref _dspSampleCursor, (long)(seconds * SampleRate));

        // 3) Обновляем общие часы — это последнее слово на этот seek.
        _clock?.Update(seconds, IsPlaying ? ClockPlaybackState.Play : ClockPlaybackState.Pause);
    }

    /// <summary>
    /// Extracts a downsampled peak envelope for waveform visualization.
    /// Returns <paramref name="bucketCount"/> values in [0, 1], where each
    /// value is the maximum absolute sample magnitude across channels within
    /// that time bucket.
    ///
    /// <para><b>Source of truth:</b> reads decoded PCM directly from BASS
    /// (not from the analysis pipeline, not from <c>PcmAvailable</c>, not
    /// from <c>.nvproj</c>). Waveform must reflect the actual audio file
    /// even in Replay / Imported mode where the analyzer is detached.</para>
    ///
    /// <para><b>Decode-only handle:</b> BASS streams do not decode while
    /// paused — <c>ChannelGetData</c> on the playback handle returns 0 bytes.
    /// Open a second, decode-only handle against the same path
    /// (<c>BassFlags.Decode | BassFlags.Float</c>): it decodes without
    /// playing, so we can pull the whole file and discard. The playback
    /// stream is not touched — position, DSP, and clock are unaffected.</para>
    ///
    /// <para><b>Cost:</b> O(frames) — one pass over the decoded stream. For
    /// a 4-minute stereo track ~50–100 ms. Call from the UI thread after
    /// Load (during which audio is paused); acceptable for MVP, one-shot
    /// per file.</para>
    /// </summary>
    public float[] ExtractWaveformPeaks(int bucketCount)
    {
        ThrowIfDisposed();
        if (bucketCount <= 0) throw new ArgumentOutOfRangeException(nameof(bucketCount));
        if (_loadedPath is null) return new float[bucketCount];

        // Decode-only handle: same file, no playback, ChannelGetData works
        // in any state (the playback handle is untouched throughout).
        var decodeHandle = Bass.CreateStream(
            _loadedPath, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (decodeHandle == 0)
        {
            // Could not open (e.g. file deleted between Load and now). Return
            // zeros rather than throwing — a waveform-less timeline is better
            // than a hard failure on a non-critical feature.
            return new float[bucketCount];
        }

        try
        {
            var totalFrames = (long)(Duration * SampleRate);
            if (totalFrames <= 0)
                return new float[bucketCount];

            var framesPerBucket = Math.Max(1L, totalFrames / bucketCount);
            var peaks = new float[bucketCount];

            const int FramesPerRead = 4096;
            var buffer = new float[FramesPerRead * Channels];

            long frameCounter = 0;
            int bucketIdx = 0;
            float bucketPeak = 0f;
            long nextBucketBoundary = framesPerBucket;

            while (bucketIdx < bucketCount)
            {
                var bytesRead = Bass.ChannelGetData(
                    decodeHandle, buffer, buffer.Length * sizeof(float));
                if (bytesRead <= 0) break;

                var floatsRead = bytesRead / sizeof(float);
                var framesRead = floatsRead / Channels;
                if (framesRead <= 0) break;

                for (int f = 0; f < framesRead && bucketIdx < bucketCount; f++)
                {
                    float framePeak = 0f;
                    for (int c = 0; c < Channels; c++)
                    {
                        var v = MathF.Abs(buffer[f * Channels + c]);
                        if (v > framePeak) framePeak = v;
                    }
                    if (framePeak > bucketPeak) bucketPeak = framePeak;

                    frameCounter++;
                    if (frameCounter >= nextBucketBoundary)
                    {
                        peaks[bucketIdx] = bucketPeak;
                        bucketIdx++;
                        bucketPeak = 0f;
                        nextBucketBoundary += framesPerBucket;
                    }
                }
            }

            // Tail: ultra-short files (totalFrames < bucketCount with
            // framesPerBucket = 1) and read-truncation both leave a few
            // trailing buckets. Repeat the last observed peak, not 0 —
            // the right edge of the waveform should not show fake silence.
            var tailValue = bucketIdx > 0 ? peaks[bucketIdx - 1] : 0f;
            while (bucketIdx < bucketCount)
            {
                peaks[bucketIdx] = tailValue;
                bucketIdx++;
            }

            return peaks;
        }
        finally
        {
            Bass.StreamFree(decodeHandle);
        }
    }

    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0 || buffer == IntPtr.Zero) return;

        var totalSamples = length / sizeof(float);
        var perChannelSamples = totalSamples / Channels;

        var genBefore = Volatile.Read(ref _seekGeneration);
        var endCursor = Interlocked.Add(ref _dspSampleCursor, perChannelSamples);
        var endPosSec = (double)endCursor / SampleRate;

        var samples = new float[totalSamples];
        Marshal.Copy(buffer, samples, 0, totalSamples);

        if (Volatile.Read(ref _seekGeneration) != genBefore)
            return;

        var frame = new PcmFrame
        {
            Samples = samples,
            Channels = Channels,
            SampleRate = SampleRate,
            PositionSeconds = endPosSec,
            Sequence = Interlocked.Increment(ref _seq),
        };

        // Единственный подписчик — AnalyzerPipeline.OnPcm, который лишь
        // кладёт фрейм в bounded Channel. Ни FFT, ни детекторов здесь больше нет.
        PcmAvailable?.Invoke(this, frame);

        // Часы обновляем независимо от подписчиков — MMF читается визуализатором
        // на суб-фреймовой частоте (фикс 4.9).
        _clock?.Update(endPosSec, ClockPlaybackState.Play);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ManagedBassAudioPlayer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != 0)
        {
            Bass.ChannelStop(_handle);
            Bass.StreamFree(_handle);
            _handle = 0;
            _loadedPath = null;
        }
        _dsp = null;
    }
}
