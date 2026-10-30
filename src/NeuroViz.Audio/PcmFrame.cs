namespace NeuroViz.Audio;

/// <summary>
/// Один блок декодированных PCM-сэмплов из аудио-колбэка.
/// Samples — interleaved по каналам, значения [-1, 1].
/// </summary>
public sealed class PcmFrame
{
    public required float[] Samples { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required double PositionSeconds { get; init; }
    public required long Sequence { get; init; }
}