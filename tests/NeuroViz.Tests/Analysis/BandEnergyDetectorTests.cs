using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class BandEnergyDetectorTests
{
    private const int FftSize = 2048;
    private const int SampleRate = 44100;
    private const double BinHz = (double)SampleRate / FftSize; // ≈ 21.53 Hz

    [Fact]
    public void LowTone_DominatesLowBand()
    {
        // 100 Hz → bin 5 (100 / 21.53 ≈ 4.64 → falls in low band).
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(5), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.low"] > 0.9f,
            $"Expected low > 0.9, got {d["band_energy.low"]:F3}");
    }

    [Fact]
    public void MidTone_DominatesMidBand()
    {
        // 1000 Hz → bin ~46.4 (mid: 250..2000 Hz).
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(46), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.mid"] > 0.9f,
            $"Expected mid > 0.9, got {d["band_energy.mid"]:F3}");
    }

    [Fact]
    public void HighTone_DominatesHighBand()
    {
        // 4000 Hz → bin ~185.7 (high: 2000..8000 Hz).
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(186), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.high"] > 0.9f,
            $"Expected high > 0.9, got {d["band_energy.high"]:F3}");
    }

    [Fact]
    public void Bands_SumToUnity_OnNonSilentFrame()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(100), sink);
        var d = sink.DrainContinuous();

        var sum = d["band_energy.low"] + d["band_energy.mid"] + d["band_energy.high"];
        Assert.InRange(sum, 0.999f, 1.001f);
    }

    [Fact]
    public void Silence_EmitsZeroForAllBands()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(
            new SpectralFrame
            {
                Magnitudes = new float[FftSize / 2 + 1],
                TimestampSeconds = 0,
                SampleRate = SampleRate,
                FftSize = FftSize,
                Sequence = 0,
            }, sink);

        var d = sink.DrainContinuous();
        Assert.Equal(0f, d["band_energy.low"]);
        Assert.Equal(0f, d["band_energy.mid"]);
        Assert.Equal(0f, d["band_energy.high"]);
    }

    [Fact]
    public void IndependentOfLoudness_SameDistribution()
    {
        // Same tone at 10x magnitude → identical normalized distribution.
        var sink1 = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(100, magnitude: 1f), sink1);
        var d1 = sink1.DrainContinuous();

        var sink2 = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrameAtBin(100, magnitude: 10f), sink2);
        var d2 = sink2.DrainContinuous();

        Assert.Equal(d1["band_energy.low"], d2["band_energy.low"], 4);
        Assert.Equal(d1["band_energy.mid"], d2["band_energy.mid"], 4);
        Assert.Equal(d1["band_energy.high"], d2["band_energy.high"], 4);
    }

    [Fact]
    public void Reset_NoStateToClear_DoesNotThrow()
    {
        var detector = new BandEnergyDetector();
        detector.Reset();          // must not throw
        detector.Reset();
    }

    [Fact]
    public void Ctor_RejectsNonAscendingBands()
    {
        Assert.Throws<ArgumentException>(() => new BandEnergyDetector(lowHi: 10, midHi: 2000, highHi: 8000));
        Assert.Throws<ArgumentException>(() => new BandEnergyDetector(lowLo: -1));
    }

    private static SpectralFrame MakeFrameAtBin(int bin, float magnitude = 100f)
    {
        var mags = new float[FftSize / 2 + 1];
        mags[bin] = magnitude;
        return new SpectralFrame
        {
            Magnitudes = mags,
            TimestampSeconds = 0,
            SampleRate = SampleRate,
            FftSize = FftSize,
            Sequence = 0,
        };
    }
}
