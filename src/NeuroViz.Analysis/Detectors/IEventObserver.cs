using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Observes every event the pipeline emits and may return derived events
/// (e.g. <see cref="StreamBoundaryDetector"/> turns novelty events into
/// stream boundary events). Derived events are written to the same sink
/// but are NOT re-observed — one level of derivation only, no recursion.
/// </summary>
public interface IEventObserver
{
    /// <summary>
    /// Called synchronously from the analyzer thread after an event has
    /// been written to the sink. Implementations must be cheap and
    /// non-throwing; any exception is counted as a detector error by the
    /// pipeline.
    /// </summary>
    IEnumerable<AudioEvent> OnEventEmitted(AudioEvent ev);
}
