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

    /// <summary>
    /// Zero-crossing rate over the analysis window, in [0, 1].
    /// Roughly: 0.02–0.20 = voice; 0.005 = bass pure tone;
    /// 0.5 = white noise. Computed by <see cref="StftBuffer"/> from the
    /// raw ring buffer (time domain), so — unlike magnitudes — it
    /// preserves phase-crossing information. 0 for hand-constructed
    /// frames in tests. See <see cref="Detectors.VadDetector"/>.
    /// </summary>
    public float ZeroCrossingRate { get; init; } = 0f;
}
