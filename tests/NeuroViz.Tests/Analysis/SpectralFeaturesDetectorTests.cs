using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class SpectralFeaturesDetectorTests
{
    [Fact]
    public void EmitsBothKeys_OnNonSilentFrame()
    {
        var sink = new EventSink();
        new SpectralFeaturesDetector().ProcessContinuous(MakeSineFrame(bin: 100), sink);

        var dict = sink.DrainContinuous();
        Assert.True(dict.ContainsKey("spectral_centroid"));
        Assert.True(dict.ContainsKey("harmonicity"));
        Assert.InRange(dict["spectral_centroid"], 0f, 1f);
        Assert.InRange(dict["harmonicity"], 0f, 1f);
    }

    [Fact]
    public void Silence_EmitsNothing()
    {
        var sink = new EventSink();
        new SpectralFeaturesDetector().ProcessContinuous(
            new SpectralFrame
            {
                Magnitudes = new float[1025],
                TimestampSeconds = 0,
                SampleRate = 44100,
                FftSize = 2048,
                Sequence = 0,
            }, sink);
        Assert.Empty(sink.DrainContinuous());
    }

    [Fact]
    public void HigherFrequencyTone_HigherCentroid()
    {
        var sink = new EventSink();
        var det = new SpectralFeaturesDetector();

        det.ProcessContinuous(MakeSineFrame(bin: 50), sink);
        var low = sink.DrainContinuous()["spectral_centroid"];

        det.ProcessContinuous(MakeSineFrame(bin: 400), sink);
        var high = sink.DrainContinuous()["spectral_centroid"];

        Assert.True(high > low, $"centroid(bin=400)={high:F3} should > centroid(bin=50)={low:F3}");
    }

    [Fact]
    public void PureTone_HigherHarmonicity_ThanNoise()
    {
        var sink = new EventSink();
        var det = new SpectralFeaturesDetector();

        det.ProcessContinuous(MakeSineFrame(bin: 100), sink);
        var tone = sink.DrainContinuous()["harmonicity"];

        det.ProcessContinuous(MakeNoiseFrame(), sink);
        var noise = sink.DrainContinuous()["harmonicity"];

        Assert.True(tone > noise, $"harmonicity(tone)={tone:F3} should > harmonicity(noise)={noise:F3}");
    }

    [Fact]
    public void SameFrame_ProducesStableOutput()
    {
        var det = new SpectralFeaturesDetector();
        var frame = MakeSineFrame(bin: 100);

        var s1 = new EventSink(); det.ProcessContinuous(frame, s1); var d1 = s1.DrainContinuous();
        var s2 = new EventSink(); det.ProcessContinuous(frame, s2); var d2 = s2.DrainContinuous();

        Assert.Equal(d1["spectral_centroid"], d2["spectral_centroid"]);
        Assert.Equal(d1["harmonicity"], d2["harmonicity"]);
    }

    private static SpectralFrame MakeSineFrame(int bin, int fftSize = 2048)
    {
        var mags = new float[fftSize / 2 + 1];
        mags[bin] = 100f;
        return new SpectralFrame
        {
            Magnitudes = mags,
            TimestampSeconds = 0,
            SampleRate = 44100,
            FftSize = fftSize,
            Sequence = 0,
        };
    }

    private static SpectralFrame MakeNoiseFrame(int fftSize = 2048)
    {
        var rng = new Random(42);
        var mags = new float[fftSize / 2 + 1];
        for (int i = 1; i < mags.Length; i++)
            mags[i] = 0.5f + (float)rng.NextDouble() * 0.5f;
        return new SpectralFrame
        {
            Magnitudes = mags,
            TimestampSeconds = 0,
            SampleRate = 44100,
            FftSize = fftSize,
            Sequence = 0,
        };
    }
}
