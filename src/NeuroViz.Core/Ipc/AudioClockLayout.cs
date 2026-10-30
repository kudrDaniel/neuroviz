namespace NeuroViz.Core.Ipc;

public static class AudioClockLayout
{
    public const int Size = 64;
    public const uint Magic = 0x4E56434Bu; // 'NVCK'
    public const int OffsetMagic = 0;
    public const int OffsetVersion = 4;
    public const int OffsetAudioPosTicksNs = 8;
    public const int OffsetWallClockTicksNs = 16;
    public const int OffsetPlaybackRate = 24;
    public const int OffsetState = 32;
    public const int OffsetReserved = 36;
}

public enum ClockPlaybackState { Stop = 0, Play = 1, Pause = 2 }