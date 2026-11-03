using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class NoveltyDetectorTests
{
    [Fact]
    public void FiresOnSuddenSpectrumChange()
    {
        var detector = new NoveltyDetector(historySize: 5, thresholdMult: 1.0f, refractorySeconds: 0.01);
        var events = new List<AudioEvent>();

        // 20 кадров одинакового "привычного" звука
        for (int i = 0; i < 20; i++)
            events.AddRange(detector.Process(MakeFrame(0.5f, i * 0.01)));

        // Резкий скачок громкости/спектра (сюрприз)
        events.AddRange(detector.Process(MakeFrame(2.0f, 0.21)));

        // Держим новый уровень
        for (int i = 0; i < 5; i++)
            events.AddRange(detector.Process(MakeFrame(2.0f, 0.22 + i * 0.01)));

        Assert.Contains(events, e => e.Type == EventTypes.Novelty);
    }

    [Fact]
    public void FlatSpectrum_NoNovelty()
    {
        var detector = new NoveltyDetector(historySize: 5, thresholdMult: 1.0f);
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
    public void Confidence_Distributed_NotSaturated()
    {
        // Regression for KI-1: the old variance-based formula saturated to 1.0
        // for every novelty event on sparse material. Verify percentile gives
        // a distribution with at most one saturated value (the unique max).
        var detector = new NoveltyDetector(
            alpha: 0.5f, historySize: 5, thresholdMult: 0.0f,
            refractorySeconds: 0.0, minVolume: 0.1f,
            confidenceMinHistory: 2);

        var allEvents = new List<AudioEvent>();
        double t = 0;

        // Warm up error history at steady level.
        for (int i = 0; i < 15; i++)
        {
            allEvents.AddRange(detector.Process(MakeFrame(0.5f, t)));
            t += 0.1;
        }

        // 5 level changes → 5 novelties with varied normalizedError.
        foreach (var lvl in new[] { 1.0f, 0.6f, 2.0f, 0.8f, 1.5f })
        {
            for (int i = 0; i < 3; i++)
            {
                allEvents.AddRange(detector.Process(MakeFrame(lvl, t)));
                t += 0.1;
            }
        }

        var confs = allEvents
            .Where(e => e.Type == EventTypes.Novelty)
            .Select(e => e.Confidence)
            .ToList();
        var formatted = string.Join(", ", confs.Select(c => c.ToString("F2")));

        Assert.True(confs.Count >= 3,
            $"Expected ≥3 novelties, got {confs.Count}. Conf: [{formatted}]");
        Assert.All(confs, c => Assert.InRange(c, 0f, 1f));

        // Before B1b: [1.0, 1.0, 1.0, 1.0, 1.0] (all saturated).
        // After B1b: [0.5, 0.5, 1.0, 0.67, 0.5] — at most one saturated.
        Assert.True(confs.Count(c => c >= 0.99f) <= 1,
            $"Expected ≤1 saturated, got {confs.Count(c => c >= 0.99f)}. Conf: [{formatted}]");
        Assert.True(confs.Distinct().Count() >= 3,
            $"Expected ≥3 distinct confidences. Conf: [{formatted}]");
    }

    [Fact]
    public void Reset_ClearsConfidenceHistory()
    {
        var detector = new NoveltyDetector(
            alpha: 0.5f, historySize: 5, thresholdMult: 0.0f,
            refractorySeconds: 0.0, minVolume: 0.1f,
            confidenceMinHistory: 2);

        // Drive enough events to warm up confidence past 0.5.
        double t = 0;
        for (int i = 0; i < 15; i++) { detector.Process(MakeFrame(0.5f, t)).ToList(); t += 0.1; }
        detector.Process(MakeFrame(1.0f, t)).ToList(); t += 0.1;
        detector.Process(MakeFrame(0.6f, t)).ToList(); t += 0.1;
        detector.Process(MakeFrame(2.0f, t)).ToList(); t += 0.1;

        detector.Reset();

        // After reset, history is empty → warm-up again.
        for (int i = 0; i < 15; i++) { detector.Process(MakeFrame(0.5f, t)).ToList(); t += 0.1; }
        var hit = detector.Process(MakeFrame(1.0f, t))
            .FirstOrDefault(e => e.Type == EventTypes.Novelty);

        Assert.NotNull(hit);
        Assert.Equal(0.5f, hit!.Confidence);
    }
}
