using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class BandEnergyDetectorTests
{
    [Fact]
    public void LowTone_EmitsInLowBand()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeToneFrame(100), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.low"] > 0.9f,
            $"low={d["band_energy.low"]:F3} should be >0.9");
        Assert.True(d["band_energy.mid"] < 0.1f);
        Assert.True(d["band_energy.high"] < 0.1f);
    }

    [Fact]
    public void MidTone_EmitsInMidBand()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeToneFrame(500), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.mid"] > 0.9f,
            $"mid={d["band_energy.mid"]:F3} should be >0.9");
        Assert.True(d["band_energy.low"] < 0.1f);
        Assert.True(d["band_energy.high"] < 0.1f);
    }

    [Fact]
    public void HighTone_EmitsInHighBand()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeToneFrame(3000), sink);
        var d = sink.DrainContinuous();

        Assert.True(d["band_energy.high"] > 0.9f,
            $"high={d["band_energy.high"]:F3} should be >0.9");
        Assert.True(d["band_energy.low"] < 0.1f);
        Assert.True(d["band_energy.mid"] < 0.1f);
    }

    [Fact]
    public void ThreeBandsSumToOne_WhenEnergyInBandsOnly()
    {
        // Three equal-magnitude bins, one in each band.
        // Bin 5  = 107 Hz (low);  bin 50 = 1076 Hz (mid);  bin 200 = 4306 Hz (high).
        var mags = new float[1025];
        mags[5] = 100f;
        mags[50] = 100f;
        mags[200] = 100f;

        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrame(mags), sink);
        var d = sink.DrainContinuous();

        float sum = d["band_energy.low"] + d["band_energy.mid"] + d["band_energy.high"];
        Assert.Equal(1.0f, sum, 2);

        // Each contributes 1/3.
        Assert.Equal(1f / 3f, d["band_energy.low"], 2);
        Assert.Equal(1f / 3f, d["band_energy.mid"], 2);
        Assert.Equal(1f / 3f, d["band_energy.high"], 2);
    }

    [Fact]
    public void BoundaryBin_CountedOnce()
    {
        // Bin 12 = 258 Hz — right at the low/mid boundary.
        // Half-open intervals [1,12), [12,93), [93,372): bin 12 → mid only.
        var mags = new float[1025];
        mags[12] = 100f;

        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrame(mags), sink);
        var d = sink.DrainContinuous();

        Assert.Equal(0f, d["band_energy.low"]);
        Assert.Equal(1f, d["band_energy.mid"]);
        Assert.Equal(0f, d["band_energy.high"]);
    }

    [Fact]
    public void Silence_EmitsZeros()
    {
        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrame(new float[1025]), sink);
        var d = sink.DrainContinuous();

        Assert.Equal(0f, d["band_energy.low"]);
        Assert.Equal(0f, d["band_energy.mid"]);
        Assert.Equal(0f, d["band_energy.high"]);
    }

    [Fact]
    public void Fractions_BoundedToUnitInterval()
    {
        // White noise — energy spread across all bins.
        var rng = new Random(7);
        var mags = new float[1025];
        for (int i = 1; i < mags.Length; i++)
            mags[i] = (float)rng.NextDouble();

        var sink = new EventSink();
        new BandEnergyDetector().ProcessContinuous(MakeFrame(mags), sink);
        var d = sink.DrainContinuous();

        Assert.InRange(d["band_energy.low"], 0f, 1f);
        Assert.InRange(d["band_energy.mid"], 0f, 1f);
        Assert.InRange(d["band_energy.high"], 0f, 1f);

        // And their sum should be < 1 — the spectrum has bins outside the
        // three bands (bins 1-4 and 372+, plus noise in the gap between
        // 8 kHz and Nyquist at 22 kHz).
        float sum = d["band_energy.low"] + d["band_energy.mid"] + d["band_energy.high"];
        Assert.True(sum < 1f, $"sum={sum:F3} should be <1 for white noise");
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static SpectralFrame MakeToneFrame(double freqHz)
    {
        var mags = new float[1025];
        int bin = (int)Math.Round(freqHz * 2048.0 / 44100.0);
        if (bin >= 1 && bin < mags.Length) mags[bin] = 100f;
        return MakeFrame(mags);
    }

    private static SpectralFrame MakeFrame(float[] mags) => new()
    {
        Magnitudes = mags,
        TimestampSeconds = 0,
        SampleRate = 44100,
        FftSize = 2048,
        Sequence = 0,
    };
}
