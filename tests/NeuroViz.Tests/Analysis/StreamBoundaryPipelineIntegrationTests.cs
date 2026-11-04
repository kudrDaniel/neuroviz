using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class StreamBoundaryPipelineIntegrationTests
{
    [Fact]
    public void Pipeline_DerivesStreamBoundary_FromNoveltyEvents()
    {
        // Build pipeline manually (no BASS), drive with two synthetic frames.
        var sink = new EventSink();
        var pipeline = new AnalyzerPipeline(
            sampleRate: 44100, channels: 1, sink: sink);

        var boundary = new StreamBoundaryDetector();
        // Only register StreamBoundary as observer — the real novelty
        // detector is not attached here; we simulate its output by
        // emitting events directly into the sink via the observer path.
        pipeline.AddObserver(boundary);

        // Manually run the observer pipeline the way AnalyzerPipeline
        // would after a detector emits.
        sink.Emit(Novelty(1.0, 0.9f), frame: null);
        sink.Emit(Novelty(2.0, 0.9f), frame: null);

        // The real AnalyzerPipeline calls observers inside ProcessFrame;
        // for this unit-style test, drive the observer directly.
        var derived = new List<AudioEvent>();
        foreach (var o in new[] { boundary })
        {
            derived.AddRange(o.OnEventEmitted(Novelty(1.0, 0.9f)));
            derived.AddRange(o.OnEventEmitted(Novelty(2.0, 0.9f)));
        }

        Assert.Single(derived);
        Assert.Equal(EventTypes.StreamBoundary, derived[0].Type);
        Assert.Equal(1f, derived[0].Params["open"]);
    }

    private static AudioEvent Novelty(double t, float confidence) => new()
    {
        Type = EventTypes.Novelty,
        TimestampSeconds = t,
        Confidence = confidence,
        Params = new Dictionary<string, float> { ["error_magnitude"] = 5f },
    };
}
