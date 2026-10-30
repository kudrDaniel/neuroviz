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
}