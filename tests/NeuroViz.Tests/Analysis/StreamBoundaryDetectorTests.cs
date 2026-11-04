using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class StreamBoundaryDetectorTests
{
    // ── Open rules ──────────────────────────────────────────────────

    [Fact]
    public void SingleStrongNovelty_DoesNotOpen()
    {
        var det = new StreamBoundaryDetector();
        var events = det.OnEventEmitted(Novelty(t: 1.0, confidence: 0.9f)).ToList();
        Assert.Empty(events);
    }

    [Fact]
    public void TwoStrongNoveltiesInWindow_Opens()
    {
        var det = new StreamBoundaryDetector();
        Assert.Empty(det.OnEventEmitted(Novelty(1.0, 0.9f)));

        var opened = det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();

        var ev = Assert.Single(opened);
        Assert.Equal(EventTypes.StreamBoundary, ev.Type);
        Assert.Equal(1f, ev.Params["open"]);
        Assert.Equal(2.0, ev.TimestampSeconds, 3);
    }

    [Fact]
    public void TwoStrongNoveltiesTooFarApart_DoesNotOpen()
    {
        var det = new StreamBoundaryDetector(openWindowSeconds: 4.0);
        Assert.Empty(det.OnEventEmitted(Novelty(1.0, 0.9f)));
        // 5s gap > 4s window: first pruned before second arrives.
        Assert.Empty(det.OnEventEmitted(Novelty(6.0, 0.9f)));
    }

    [Fact]
    public void TwoWeakNovelties_DoesNotOpen()
    {
        var det = new StreamBoundaryDetector();
        // confidence 0.5 < 0.75 and no error_magnitude → weak.
        Assert.Empty(det.OnEventEmitted(Novelty(1.0, 0.5f)));
        Assert.Empty(det.OnEventEmitted(Novelty(2.0, 0.5f)));
    }

    [Fact]
    public void StrongByMagnitude_Opens_EvenWithLowConfidence()
    {
        // error_magnitude >= 4.0 triggers open even if percentile is low.
        var det = new StreamBoundaryDetector();
        Assert.Empty(det.OnEventEmitted(Novelty(1.0, confidence: 0.5f, magnitude: 5.0f)));
        var opened = det.OnEventEmitted(Novelty(2.0, confidence: 0.5f, magnitude: 5.0f));
        Assert.Single(opened);
    }

    [Fact]
    public void NonNoveltyEvent_Ignored()
    {
        var det = new StreamBoundaryDetector();
        var onset = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.9f, TimestampSeconds = 1.0 };
        Assert.Empty(det.OnEventEmitted(onset));
        Assert.Empty(det.OnEventEmitted(onset));
    }

    // ── Refresh & close ─────────────────────────────────────────────

    [Fact]
    public void AlreadyOpen_MoreNoveltyDoesNotReopen()
    {
        var det = new StreamBoundaryDetector();
        det.OnEventEmitted(Novelty(1.0, 0.9f)).ToList();
        det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();   // opens

        // More strong novelties while open → no extra boundary events.
        Assert.Empty(det.OnEventEmitted(Novelty(5.0, 0.9f)));
        Assert.Empty(det.OnEventEmitted(Novelty(7.0, 0.9f)));
    }

    [Fact]
    public void Timeout_Closes()
    {
        var det = new StreamBoundaryDetector(closeAfterSeconds: 8.0);
        det.OnEventEmitted(Novelty(1.0, 0.9f)).ToList();
        det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();   // opens at t=2

        // Frame at t=9 → 7s since last novelty, not yet.
        Assert.Empty(det.Process(Frame(9.0)));

        // Frame at t=10 → 8s → close.
        var closed = det.Process(Frame(10.0)).ToList();
        var ev = Assert.Single(closed);
        Assert.Equal(EventTypes.StreamBoundary, ev.Type);
        Assert.Equal(0f, ev.Params["open"]);
    }

    [Fact]
    public void RefreshAfterOpen_ExtendsTimeout()
    {
        var det = new StreamBoundaryDetector(closeAfterSeconds: 8.0);
        det.OnEventEmitted(Novelty(1.0, 0.9f)).ToList();
        det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();   // opens at t=2

        det.OnEventEmitted(Novelty(5.0, 0.9f)).ToList();   // refresh → lastStrong=5
        Assert.Empty(det.OnEventEmitted(Novelty(5.0, 0.9f))); // no extra event

        Assert.Empty(det.Process(Frame(12.0)));              // 7s → no close
        var closed = det.Process(Frame(13.5)).ToList();      // 8.5s → close
        Assert.Single(closed);
    }

    [Fact]
    public void AfterClose_RefractoryBlocksImmediateReopen()
    {
        var det = new StreamBoundaryDetector(
            refractorySeconds: 2.0, closeAfterSeconds: 8.0);
        det.OnEventEmitted(Novelty(1.0, 0.9f)).ToList();
        det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();   // open at t=2
        det.Process(Frame(10.0)).ToList();                  // close at t=10

        // Two strong novelties within refractory — count=2 but lastBoundary=10.
        Assert.Empty(det.OnEventEmitted(Novelty(10.5, 0.9f)));
        Assert.Empty(det.OnEventEmitted(Novelty(11.0, 0.9f)));   // 1s after close, still blocked

        // t=13: 3s after close, refractory elapsed → open.
        Assert.Empty(det.OnEventEmitted(Novelty(12.5, 0.9f)));   // count=1
        var opened = det.OnEventEmitted(Novelty(13.0, 0.9f));    // count=2, refractory ok
        Assert.Single(opened);
    }

    // ── Reset ───────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsStickyState()
    {
        var det = new StreamBoundaryDetector();
        det.OnEventEmitted(Novelty(1.0, 0.9f)).ToList();
        det.OnEventEmitted(Novelty(2.0, 0.9f)).ToList();   // open

        det.Reset();

        // After reset, first strong novelty doesn't open (need two again).
        Assert.Empty(det.OnEventEmitted(Novelty(5.0, 0.9f)));
        var opened = det.OnEventEmitted(Novelty(6.0, 0.9f));
        Assert.Single(opened);
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static AudioEvent Novelty(double t, float confidence, float magnitude = 0f)
    {
        var p = new Dictionary<string, float>();
        if (magnitude > 0f) p["error_magnitude"] = magnitude;
        return new AudioEvent
        {
            Type = EventTypes.Novelty,
            TimestampSeconds = t,
            Confidence = confidence,
            SourceLayer = 2,
            Params = p,
        };
    }

    private static SpectralFrame Frame(double t) => new()
    {
        Magnitudes = new float[1025],
        TimestampSeconds = t,
        SampleRate = 44100,
        FftSize = 2048,
        Sequence = 0,
    };
}
