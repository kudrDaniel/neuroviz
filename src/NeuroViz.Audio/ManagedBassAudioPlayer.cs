using System.Runtime.InteropServices;
using ManagedBass;
using BassState = ManagedBass.PlaybackState;

namespace NeuroViz.Audio;

public sealed class ManagedBassAudioPlayer : IAudioPlayer
{
    private readonly AudioClockWriter? _clock;
    private int _handle;
    private long _seq;
    private long _dspSampleCursor;   // <-- НОВОЕ: сэмплов на канал, прошедших DSP
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

        var info = Bass.ChannelGetInfo(_handle);
        SampleRate = info.Frequency;
        Channels = info.Channels;
        _seq = 0;
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
        Bass.ChannelStop(_handle);
        Interlocked.Exchange(ref _dspSampleCursor, 0);
        _clock?.Update(0, ClockPlaybackState.Stop);
    }

    public void Seek(double seconds)
    {
        ThrowIfDisposed();
        if (_handle == 0) return;
        var bytes = Bass.ChannelSeconds2Bytes(_handle, seconds);
        Bass.ChannelSetPosition(_handle, bytes);
        Interlocked.Exchange(ref _dspSampleCursor, (long)(seconds * SampleRate));
        _clock?.Update(seconds, IsPlaying ? ClockPlaybackState.Play : ClockPlaybackState.Pause);
    }

    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0 || buffer == IntPtr.Zero) return;

        var totalSamples = length / sizeof(float);
        var perChannelSamples = totalSamples / Channels;

        // Позиция КОНЦА буфера — это ровно тот момент, до которого доанализированы данные.
        var endPosSec = (double)(Interlocked.Read(ref _dspSampleCursor) + perChannelSamples) / SampleRate;

        var samples = new float[totalSamples];
        Marshal.Copy(buffer, samples, 0, totalSamples);

        var frame = new PcmFrame
        {
            Samples = samples,
            Channels = Channels,
            SampleRate = SampleRate,
            PositionSeconds = endPosSec,
            Sequence = Interlocked.Increment(ref _seq),
        };

        Interlocked.Add(ref _dspSampleCursor, perChannelSamples);

        PcmAvailable?.Invoke(this, frame);
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
        }
        _dsp = null;
    }
}