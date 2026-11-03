using NeuroViz.Analysis.Detectors;
using NeuroViz.Audio;
using NeuroViz.Core.Events;

namespace NeuroViz.Tests.Analysis;

public class AzimuthDetectorTests
{
    [Fact]
    public void Mono_DoesNotFire()
    {
        var detector = new AzimuthDetector(refractorySeconds: 0.0);
        var mono = new PcmFrame
        {
            Samples = new float[1024],
            Channels = 1,
            SampleRate = 44100,
            PositionSeconds = 0.1,
            Sequence = 0,
        };
        Assert.Empty(detector.ProcessPcm(mono));
    }

    [Fact]
    public void Silence_DoesNotFire()
    {
        var detector = new AzimuthDetector(refractorySeconds: 0.0);
        Assert.Empty(detector.ProcessPcm(MakeStereoFrame(0f, 0f, 0.1)));
    }

    [Fact]
    public void EmitsEvent_OnStereoSignal()
    {
        var detector = new AzimuthDetector(refractorySeconds: 0.0);
        var ev = Assert.Single(detector.ProcessPcm(MakeStereoFrame(1f, 1f, 0.1)));
        Assert.Equal(EventTypes.AzimuthEstimate, ev.Type);
        // Raw ILD preserved in params — rules using param_from_event are safe.
        Assert.InRange(ev.Params["azimuth_norm"], -1f, 1f);
    }

    [Fact]
    public void Confidence_Distributed_NotSaturated()
    {
        var detector = new AzimuthDetector(
            refractorySeconds: 0.0, confidenceMinHistory: 2);

        var confs = new List<float>();
        double t = 0;
        // |ildNorm| values: 0, 0.5, 1.0, 1.0, 0.5.
        var ratios = new[] { (1f, 1f), (2f, 1f), (4f, 1f), (1f, 4f), (1f, 2f) };
        foreach (var (l, r) in ratios)
        {
            foreach (var ev in detector.ProcessPcm(MakeStereoFrame(l, r, t)))
            {
                if (ev.Type == EventTypes.AzimuthEstimate)
                    confs.Add(ev.Confidence);
            }
            t += 0.1;
        }

        var formatted = string.Join(", ", confs.Select(c => c.ToString("F2")));

        Assert.True(confs.Count >= 3,
            $"Expected ≥3 azimuth events, got {confs.Count}. Conf: [{formatted}]");
        Assert.All(confs, c => Assert.InRange(c, 0f, 1f));

        // Before B1b: [0.0, 0.5, 1.0, 1.0, 0.5] — |ildNorm| absolute, saturates
        //             to 1.0 on fully-panned material (2.0+ dB ratio).
        // After B1b:  [0.5, 0.5, 1.0, 0.83, 0.375] — percentile of |ildNorm|.
        Assert.True(confs.Count(c => c >= 0.99f) <= 1,
            $"Expected ≤1 saturated, got {confs.Count(c => c >= 0.99f)}. Conf: [{formatted}]");
    }

    [Fact]
    public void Reset_ClearsConfidenceHistory()
    {
        var detector = new AzimuthDetector(
            refractorySeconds: 0.0, confidenceMinHistory: 2);

        detector.ProcessPcm(MakeStereoFrame(1f, 1f, 0.1)).ToList(); // 0   (warm-up → 0.5)
        detector.ProcessPcm(MakeStereoFrame(2f, 1f, 0.2)).ToList(); // 0.5 (warm-up → 0.5)
        var third = detector.ProcessPcm(MakeStereoFrame(4f, 1f, 0.3))
            .First(e => e.Type == EventTypes.AzimuthEstimate);
        Assert.Equal(1.0f, third.Confidence); // 1.0 > 0, 0.5

        detector.Reset();

        var firstAfter = detector.ProcessPcm(MakeStereoFrame(1f, 1f, 0.4))
            .First(e => e.Type == EventTypes.AzimuthEstimate);
        Assert.Equal(0.5f, firstAfter.Confidence);
    }

    private static PcmFrame MakeStereoFrame(float leftAmp, float rightAmp, double t)
    {
        var samples = new float[1024];
        for (int i = 0; i < 512; i++)
        {
            float s = MathF.Sin(2 * MathF.PI * 440 * i / 44100f);
            samples[i * 2] = s * leftAmp;
            samples[i * 2 + 1] = s * rightAmp;
        }
        return new PcmFrame
        {
            Samples = samples,
            Channels = 2,
            SampleRate = 44100,
            PositionSeconds = t,
            Sequence = 0,
        };
    }
}
