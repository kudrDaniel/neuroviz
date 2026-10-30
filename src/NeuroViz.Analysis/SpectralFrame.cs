namespace NeuroViz.Analysis;

/// <summary>
/// Один кадр STFT: magnitude-спектр (только неотрицательные частоты).
/// Magnitudes.Length = FftSize/2 + 1.
/// </summary>
public sealed class SpectralFrame
{
    public required float[] Magnitudes { get; init; }
    public required double TimestampSeconds { get; init; }
    public required int SampleRate { get; init; }
    public required int FftSize { get; init; }
    public required long Sequence { get; init; }
}