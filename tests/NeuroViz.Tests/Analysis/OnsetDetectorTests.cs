using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class OnsetDetectorTests
{
    [Fact]
    public void FiresOnSpectralJump()
    {
        var detector = new OnsetDetector(historySize: 5, thresholdMult: 1.0f);
        var events = new List<AudioEvent>();

        for (int i = 0; i < 30; i++)
            events.AddRange(detector.Process(MakeFrame(0.1f, i * 0.01)));

        events.AddRange(detector.Process(MakeFrame(1.0f, 0.31)));
        for (int i = 0; i < 5; i++)
            events.AddRange(detector.Process(MakeFrame(1.0f, 0.32 + i * 0.01)));

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(EventTypes.Onset, e.Type));
    }

    [Fact]
    public void FlatSpectrum_NoOnsets()
    {
        var detector = new OnsetDetector(historySize: 5, thresholdMult: 1.0f);
        var events = new List<AudioEvent>();
        for (int i = 0; i < 50; i++)
            events.AddRange(detector.Process(MakeFrame(0.5f, i * 0.01)));
        Assert.Empty(events);
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

    [Fact]
    public void QuietMaterial_ConfidenceDistributedNotSaturated()
    {
        // Regression for KI-1: on quiet, sparse material the old variance-based
        // formula reported confidence ≈ 1.0 for every onset. Verify that with
        // PercentileConfidence the values are distributed and only the (unique)
        // strongest event reaches 1.0.
        var detector = new OnsetDetector(
            historySize: 3,
            thresholdMult: 0.0f,      // sensitive: any flux above mean fires
            refractorySeconds: 0.0);

        var allEvents = new List<AudioEvent>();
        double t = 0;

        // Warm-up: quiet background. historySize=3 means the detector starts
        // detecting from frame 5; the 10 frames here ensure we start clean.
        for (int i = 0; i < 10; i++)
        {
            allEvents.AddRange(detector.Process(MakeFrame(0.01f, t)));
            t += 0.01;
        }

        // Varied hits with quiet inter-frames. Not monotonic — realistic music
        // has alternating strong/weak hits, not ascending-only.
        var strengths = new[] { 0.5f, 0.2f, 0.7f, 0.3f, 0.6f, 0.25f, 0.8f, 0.35f };
        foreach (var s in strengths)
        {
            allEvents.AddRange(detector.Process(MakeFrame(s, t))); t += 0.01;
            allEvents.AddRange(detector.Process(MakeFrame(0.01f, t))); t += 0.01;
        }

        var onsets = allEvents.Where(e => e.Type == EventTypes.Onset).ToList();
        var confidences = onsets.Select(e => e.Confidence).ToList();
        var formatted = string.Join(", ", confidences.Select(c => c.ToString("F2")));

        Assert.True(onsets.Count >= 6,
            $"Expected ≥6 onsets, got {onsets.Count}. Conf: [{formatted}]");

        // No saturation: at most one event (the unique strongest) hits 1.0.
        Assert.True(confidences.Count(c => c >= 0.99f) <= 1,
            $"Expected ≤1 saturated event, got {confidences.Count(c => c >= 0.99f)}. Conf: [{formatted}]");

        // All values in range.
        Assert.All(confidences, c => Assert.InRange(c, 0f, 1f));

        // Differentiation: at least 3 distinct values (warm-up 0.5, low, high).
        var distinct = confidences.Distinct().Count();
        Assert.True(distinct >= 3,
            $"Expected ≥3 distinct confidences, got {distinct}. Conf: [{formatted}]");
    }

    [Fact]
    public void Reset_ClearsConfidenceHistory()
    {
        var detector = new OnsetDetector(
            historySize: 3, thresholdMult: 0.0f, refractorySeconds: 0.0,
            confidenceMinHistory: 2);
        double t = 0;

        // Fill detector warm-up + emit enough events to warm up confidence.
        for (int i = 0; i < 10; i++)
        {
            detector.Process(MakeFrame(0.01f, t)).ToList(); t += 0.01;
        }
        detector.Process(MakeFrame(0.5f, t)).ToList(); t += 0.01;
        detector.Process(MakeFrame(0.01f, t)).ToList(); t += 0.01;
        detector.Process(MakeFrame(0.6f, t)).ToList(); t += 0.01;

        detector.Reset();

        // After reset, first new hit is back in warm-up → 0.5.
        for (int i = 0; i < 10; i++)
        {
            detector.Process(MakeFrame(0.01f, t)).ToList(); t += 0.01;
        }
        var hit = detector.Process(MakeFrame(0.5f, t))
            .FirstOrDefault(e => e.Type == EventTypes.Onset);

        Assert.NotNull(hit);
        Assert.Equal(0.5f, hit!.Confidence);
    }
}
