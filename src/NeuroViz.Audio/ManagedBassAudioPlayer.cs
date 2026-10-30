using System.Runtime.InteropServices;
using ManagedBass;

namespace NeuroViz.Audio;

public sealed class ManagedBassAudioPlayer : IAudioPlayer
{
    private readonly AudioClockWriter? _clock;
    private int _handle;
    private long _seq;
    private DSPProcedure? _dsp;      // держим делегат живым
    private bool _disposed;

    public ManagedBassAudioPlayer(AudioClockWriter? clock = null)
    {
        _clock = clock;
        BassBootstrap.EnsureInitialized();
    }

    public event EventHandler<PcmFrame>? PcmAvailable;

    public double Position
    {
        get
        {
            if (_handle == 0) return 0;
            var bytes = Bass.ChannelGetPosition(_handle);
            return Bass.ChannelBytes2Seconds(_handle, bytes);
        }
    }

    public double Duration
    {
        get
        {
            if (_handle == 0) return 0;
            var bytes = Bass.ChannelGetLength(_handle);
            return Bass.ChannelBytes2Seconds(_handle, bytes);
        }
    }

    public bool IsPlaying => _handle != 0 && Bass.ChannelIsActive(_handle) == PlaybackState.Playing;
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

        var flags = BassFlags.Float;          // 32-bit float PCM — удобно для анализа
        _handle = Bass.CreateStream(path, 0, 0, flags);
        if (_handle == 0)
            throw new InvalidOperationException($"Bass.CreateStream failed: {Bass.LastError}");

        var info = Bass.ChannelGetInfo(_handle);
        SampleRate = info.Frequency;
        Channels = info.Channels;
        _seq = 0;

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
        _clock?.Update(0, ClockPlaybackState.Stop);
    }

    public void Seek(double seconds)
    {
        ThrowIfDisposed();
        if (_handle == 0) return;
        var bytes = Bass.ChannelSeconds2Bytes(_handle, seconds);
        Bass.ChannelSetPosition(_handle, bytes);
        _clock?.Update(Position, IsPlaying ? ClockPlaybackState.Play : ClockPlaybackState.Pause);
    }

    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0 || buffer == IntPtr.Zero) return;

        var sampleCount = length / sizeof(float);
        var samples = new float[sampleCount];
        Marshal.Copy(buffer, samples, 0, sampleCount);

        var bytes = Bass.ChannelGetPosition(handle);
        var posSec = Bass.ChannelBytes2Seconds(handle, bytes);

        var frame = new PcmFrame
        {
            Samples = samples,
            Channels = Channels,
            SampleRate = SampleRate,
            PositionSeconds = posSec,
            Sequence = Interlocked.Increment(ref _seq),
        };

        PcmAvailable?.Invoke(this, frame);
        _clock?.Update(posSec, ClockPlaybackState.Play);
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