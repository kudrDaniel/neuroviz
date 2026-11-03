namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Three-band energy, emitted as continuous values per frame:
/// <list type="bullet">
///   <item><c>band_energy.low</c>  — 20..250 Hz</item>
///   <item><c>band_energy.mid</c>  — 250..2000 Hz</item>
///   <item><c>band_energy.high</c> — 2000..8000 Hz</item>
/// </list>
///
/// <para><b>Normalization:</b> each band's RMS is divided by the total
/// RMS across all bands, so the three values always sum to ~1.0 and are
/// independent of overall loudness. The visualizer can scale particles
/// by <c>band_energy.low</c> whether the track is quiet or loud — the
/// metric is <i>relative spectral distribution</i>, not absolute energy.</para>
///
/// <para><b>Why relative:</b> absolute band energy would saturate on
/// loud tracks and collapse on quiet ones (same class of bug as KI-1
/// for events). The rule of thumb from B1/B2: continuous metrics that
/// drive visuals should be bounded and material-independent.</para>
///
/// <para><b>Silence:</b> when total RMS falls below
/// <see cref="SilenceThreshold"/>, emit 0.0 for all three keys rather
/// than nothing. This actively pulls the visualizer to a "no energy"
/// state instead of freezing at the last loud-frame values (compare
/// with spectral_centroid in B2, where silence emits nothing by design
/// because centroid has no meaningful zero).</para>
///
/// <para><b>Bin mapping:</b> bin index b has frequency
/// <c>b * sampleRate / fftSize</c>. For each band, we accumulate
/// <c>sum(mag[i]^2)</c> over bins in [f_lo, f_hi) and take sqrt
/// (RMS across the band's bin magnitudes). Bands below <c>minBins</c>
/// are dropped — protects against pathological fftSize / sampleRate
/// combinations where "low" has fewer than 2 bins.</para>
/// </summary>
public sealed class BandEnergyDetector : IContinuousDetector
{
    private const float SilenceThreshold = 1e-5f;
    private const int MinBins = 2;

    private readonly double _freqLowLo;
    private readonly double _freqLowHi;
    private readonly double _freqMidHi;
    private readonly double _freqHighHi;

    public BandEnergyDetector(
        double lowLo = 20,
        double lowHi = 250,
        double midHi = 2000,
        double highHi = 8000)
    {
        if (lowLo < 0 || lowHi <= lowLo || midHi <= lowHi || highHi <= midHi)
            throw new ArgumentException("band frequencies must be strictly ascending");
        _freqLowLo = lowLo;
        _freqLowHi = lowHi;
        _freqMidHi = midHi;
        _freqHighHi = highHi;
    }

    public void Reset() { /* stateless */ }

    public void ProcessContinuous(SpectralFrame frame, EventSink sink)
    {
        var mags = frame.Magnitudes;
        if (mags.Length < 2) return;

        var binHz = (double)frame.SampleRate / frame.FftSize;

        // Compute frequency range for each band in bin indices.
        int lo0 = Math.Max(1, (int)Math.Floor(_freqLowLo / binHz));
        int lo1 = Math.Min(mags.Length, (int)Math.Ceiling(_freqLowHi / binHz));
        int mid0 = lo1;
        int mid1 = Math.Min(mags.Length, (int)Math.Ceiling(_freqMidHi / binHz));
        int hi0 = mid1;
        int hi1 = Math.Min(mags.Length, (int)Math.Ceiling(_freqHighHi / binHz));

        float rmsLow = RmsInRange(mags, lo0, lo1);
        float rmsMid = RmsInRange(mags, mid0, mid1);
        float rmsHigh = RmsInRange(mags, hi0, hi1);

        var total = rmsLow + rmsMid + rmsHigh;
        if (total < SilenceThreshold)
        {
            // Active zero: pulls visualizer to "no energy" state.
            sink.EmitContinuous("band_energy.low", 0f);
            sink.EmitContinuous("band_energy.mid", 0f);
            sink.EmitContinuous("band_energy.high", 0f);
            return;
        }

        sink.EmitContinuous("band_energy.low", rmsLow / total);
        sink.EmitContinuous("band_energy.mid", rmsMid / total);
        sink.EmitContinuous("band_energy.high", rmsHigh / total);
    }

    private static float RmsInRange(float[] mags, int start, int end)
    {
        if (end - start < MinBins) return 0f;
        double sumSq = 0;
        for (int i = start; i < end; i++) sumSq += (double)mags[i] * mags[i];
        return (float)Math.Sqrt(sumSq / (end - start));
    }
}
