using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class PitchDetectorTests
{
    [Fact]
    public void EmitsEvent_OnCleanPitchedSpectrum()
    {
        var detector = new PitchDetector(refractorySeconds: 0.0);
        var events = detector.Process(MakePitchedFrame(20f, 0.1)).ToList();

        var pitch = Assert.Single(events);
        Assert.Equal(EventTypes.PitchSalience, pitch.Type);
        // Raw maxHps is preserved in params (KI-1 escape hatch).
        Assert.True(pitch.Params["hps_strength"] > 0f);
        Assert.InRange(pitch.Params["frequency_hz"], 50f, 4000f);
    }

    [Fact]
    public void SilencesDoNotFire()
    {
        var detector = new PitchDetector(refractorySeconds: 0.0);
        var silence = new SpectralFrame
        {
            Magnitudes = new float[1025],
            TimestampSeconds = 0.1,
            SampleRate = 44100,
            FftSize = 2048,
            Sequence = 0,
        };
        Assert.Empty(detector.Process(silence));
    }

    [Fact]
    public void Confidence_Distributed_NotSaturated()
    {
        // Regression for KI-1: old formula maxHps/(maxHps+10) was ~0.95-1.0
        // for every clean piano note (HPS in hundreds). Verify percentile.
        var detector = new PitchDetector(
            refractorySeconds: 0.0, confidenceMinHistory: 2);

        var confs = new List<float>();
        double t = 0;
        foreach (var peak in new[] { 20f, 5f, 50f, 8f, 30f })
        {
            foreach (var ev in detector.Process(MakePitchedFrame(peak, t)))
            {
                if (ev.Type == EventTypes.PitchSalience)
                    confs.Add(ev.Confidence);
            }
            t += 0.1;
        }

        var formatted = string.Join(", ", confs.Select(c => c.ToString("F2")));

        Assert.True(confs.Count >= 3,
            $"Expected ≥3 pitches, got {confs.Count}. Conf: [{formatted}]");
        Assert.All(confs, c => Assert.InRange(c, 0f, 1f));
        // Before B1b: [~1.0, ~1.0, ~1.0, ~1.0, ~1.0]
        // After B1b:  [0.5, 0.5, 1.0, 0.33, 0.75]
        Assert.True(confs.Count(c => c >= 0.99f) <= 1,
            $"Expected ≤1 saturated, got {confs.Count(c => c >= 0.99f)}. Conf: [{formatted}]");
    }

    [Fact]
    public void Reset_ClearsConfidenceHistory()
    {
        var detector = new PitchDetector(
            refractorySeconds: 0.0, confidenceMinHistory: 2);

        detector.Process(MakePitchedFrame(20f, 0.1)).ToList();
        detector.Process(MakePitchedFrame(50f, 0.2)).ToList();
        var third = detector.Process(MakePitchedFrame(100f, 0.3))
            .First(e => e.Type == EventTypes.PitchSalience);
        Assert.Equal(1.0f, third.Confidence); // 100^4 > 20^4, 50^4

        detector.Reset();

        var firstAfter = detector.Process(MakePitchedFrame(20f, 0.4))
            .First(e => e.Type == EventTypes.PitchSalience);
        Assert.Equal(0.5f, firstAfter.Confidence); // warm-up again
    }

    private static SpectralFrame MakePitchedFrame(float peakMag, double t)
    {
        // 4-harmonic stack at bins 10/20/30/40 → HPS = peak^4.
        // bin 10 * 44100 / 2048 ≈ 215 Hz, in [50, 4000].
        var mags = new float[1025];
        mags[10] = peakMag;
        mags[20] = peakMag;
        mags[30] = peakMag;
        mags[40] = peakMag;
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
