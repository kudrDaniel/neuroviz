using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using NeuroViz.Core.Ipc;

namespace NeuroViz.Audio;

public sealed class AudioClockWriter : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public AudioClockWriter()
    {
        var path = Path.Combine(Path.GetTempPath(), IpcConstants.AudioClockMapName + ".bin");
        _mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Create, IpcConstants.AudioClockMapName,
            AudioClockLayout.Size, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, AudioClockLayout.Size);
        _view.Write(AudioClockLayout.OffsetMagic, AudioClockLayout.Magic);
        _view.Write(AudioClockLayout.OffsetVersion, 1);
    }

    public void Update(double audioPositionSeconds, ClockPlaybackState state, double rate = 1.0)
    {
        var audioTicks = (long)(audioPositionSeconds * 1_000_000_000L);
        var wallTicks = (long)(_sw.Elapsed.TotalSeconds * 1_000_000_000L);
        var rateX = (long)(rate * 1_000_000L);

        _view.Write(AudioClockLayout.OffsetAudioPosTicksNs, audioTicks);
        _view.Write(AudioClockLayout.OffsetWallClockTicksNs, wallTicks);
        _view.Write(AudioClockLayout.OffsetPlaybackRate, rateX);
        _view.Write(AudioClockLayout.OffsetState, (int)state);
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}