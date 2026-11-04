namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Implemented by detectors that carry higher-level state (e.g. sticky
/// open/close) that should survive a seek. Regular detectors reset their
/// frame-history on seek; seek-aware detectors adjust their time references
/// by the seek delta instead of clearing.
/// </summary>
public interface ISeekAwareDetector
{
    void OnSeek(double oldPositionSeconds, double newPositionSeconds);
}
