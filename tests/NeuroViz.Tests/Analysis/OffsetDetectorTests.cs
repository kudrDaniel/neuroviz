using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class OffsetDetectorTests
{
    [Fact]
    public void FiresOnSpectralDrop()
    {
        var detector = new OffsetDetector(historySize: 5, thresholdMult: 1.0f);
        var events = new List<AudioEvent>();

        // Steady spectrum → history of decline = 0.
        for (int i = 0; i < 30; i++)
            events.AddRange(detector.Process(MakeFrame(1.0f, i * 0.01)));

        // Sudden collapse.
        events.AddRange(detector.Process(MakeFrame(0.1f, 0.31)));
        for (int i = 0; i < 5; i++)
            events.AddRange(detector.Process(MakeFrame(0.1f, 0.32 + i * 0.01)));

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(EventTypes.Offset, e.Type));
    }

    [Fact]
    public void RisingEdge_DoesNotFire()
    {
        // Mirror-image pattern of OnsetDetectorTests.FiresOnSpectralJump.
        // Positive flux must produce decline = 0 and never trigger offset.
        var detector = new OffsetDetector(historySize: 5, thresholdMult: 1.0f);
        var events = new List<AudioEvent>();

        for (int i = 0; i < 30; i++)
            events.AddRange(detector.Process(MakeFrame(0.1f, i * 0.01)));

        events.AddRange(detector.Process(MakeFrame(1.0f, 0.31)));
        for (int i = 0; i < 5; i++)
            events.AddRange(detector.Process(MakeFrame(1.0f, 0.32 + i * 0.01)));

        Assert.Empty(events);
    }

    [Fact]
    public void FlatSpectrum_NoOffsets()
    {
        var detector = new OffsetDetector(historySize: 5, thresholdMult: 1.0f);
        var events = new List<AudioEvent>();
        for (int i = 0; i < 50; i++)
            events.AddRange(detector.Process(MakeFrame(0.5f, i * 0.01)));
        Assert.Empty(events);
    }

    [Fact]
    public void Confidence_Distributed_NotSaturated()
    {
        // Same shape as OnsetDetectorTests.QuietMaterial_ConfidenceDistributed
        // _NotSaturated, but on non-monotonic *drops*. Expected after B4:
        // [0.50, 0.50, 0.50, 0.67, 1.00] — 5 fires, 1 saturated (the unique
        // steepest drop), 3 distinct values. Before B4 no offset detector
        // existed; the first percentile has nothing to compare to (0.50).
        var detector = new OffsetDetector(
            historySize: 3, thresholdMult: 0.0f, refractorySeconds: 0.0);

        var allEvents = new List<AudioEvent>();
        double t = 0;

        for (int i = 0; i < 10; i++)
        {
            allEvents.AddRange(detector.Process(MakeFrame(0.5f, t)));
            t += 0.01;
        }

        // Levels alternate above/below 0.5 so positive and negative drops
        // both occur (some are declines, some are rises and produce no
        // offset — exercising the isFallingEdge direction check).
        var levels = new[] { 0.4f, 0.2f, 0.7f, 0.3f, 0.6f, 0.25f, 0.8f, 0.35f };
        foreach (var lvl in levels)
        {
            allEvents.AddRange(detector.Process(MakeFrame(lvl, t))); t += 0.01;
            allEvents.AddRange(detector.Process(MakeFrame(lvl, t))); t += 0.01;
        }

        var offsets = allEvents.Where(e => e.Type == EventTypes.Offset).ToList();
        var confs = offsets.Select(e => e.Confidence).ToList();
        var formatted = string.Join(", ", confs.Select(c => c.ToString("F2")));

        Assert.True(offsets.Count >= 5,
            $"Expected ≥5 offsets, got {offsets.Count}. Conf: [{formatted}]");
        Assert.All(confs, c => Assert.InRange(c, 0f, 1f));
        Assert.True(confs.Count(c => c >= 0.99f) <= 1,
            $"Expected ≤1 saturated, got {confs.Count(c => c >= 0.99f)}. Conf: [{formatted}]");
        Assert.True(confs.Distinct().Count() >= 3,
            $"Expected ≥3 distinct confidences. Conf: [{formatted}]");
    }

    [Fact]
    public void Reset_ClearsConfidenceHistory()
    {
        var detector = new OffsetDetector(
            historySize: 3, thresholdMult: 0.0f, refractorySeconds: 0.0,
            confidenceMinHistory: 2);
        double t = 0;

        // Fill frame history + confidence warm-up.
        for (int i = 0; i < 10; i++) { detector.Process(MakeFrame(0.5f, t)).ToList(); t += 0.01; }
        detector.Process(MakeFrame(0.3f, t)).ToList(); t += 0.01;   // decline=0.2 → fire
        detector.Process(MakeFrame(0.3f, t)).ToList(); t += 0.01;   // decline=0
        detector.Process(MakeFrame(0.1f, t)).ToList(); t += 0.01;   // decline=0.2 → fire

        detector.Reset();

        // After reset, first fire is back in warm-up → 0.5.
        for (int i = 0; i < 10; i++) { detector.Process(MakeFrame(0.5f, t)).ToList(); t += 0.01; }
        var hit = detector.Process(MakeFrame(0.1f, t))
            .FirstOrDefault(e => e.Type == EventTypes.Offset);

        Assert.NotNull(hit);
        Assert.Equal(0.5f, hit!.Confidence);
    }

    private static SpectralFrame MakeFrame(float magnitude, double t)
    {
        var mags = new float[1025];
        Array.Fill(mags, magnitude);
        return new SpectralFrame
        {
            Magnitudes = mags,
            TimestampSeconds = t,
            SampleRate = 44100,
            FftSize = 2048,
            Sequence = 0,
        };
    }
}
