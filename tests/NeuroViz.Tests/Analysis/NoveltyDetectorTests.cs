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
}