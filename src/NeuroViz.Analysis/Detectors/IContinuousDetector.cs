namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// A per-frame continuous metric producer. Called on every <see cref="SpectralFrame"/>
/// on the analyzer thread. Emits zero or more (key, value) pairs into
/// <see cref="EventSink"/>. No events, no confidence, no decay.
///
/// <para>Contrast with <see cref="IEventDetector"/>: events are discrete,
/// carry <c>Confidence</c>, and decay via <c>ParamValue.EvaluateAt</c>.
/// Continuous values are sampled every frame and used directly by the
/// visualizer.</para>
/// </summary>
public interface IContinuousDetector
{
    void ProcessContinuous(SpectralFrame frame, EventSink sink);
    void Reset();
}
