namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Per-frame band energy split into three bands, emitted as continuous
/// values. Each value is the fraction of total (non-DC) spectral energy
/// that falls into that band — a value in [0, 1]. Sum over the three
/// bands is ≤ 1 (the rest lives in sub-20 Hz and super-8 kHz bins).
///
/// <para><b>Why fractions, not RMS:</b> an absolute RMS would make the
/// visualizer loudness-dependent — quiet tracks would show near-zero
/// energies on every band, loud tracks would saturate. Fractions capture
/// the tonal character (bass-heavy vs bright) independently of volume.</para>
///
/// <para><b>Silence:</b> emits (0, 0, 0). Unlike <see cref="SpectralFeaturesDetector"/>,
/// which skips on silence (a centroid on silence is undefined), band energy
/// on silence is well-defined as zero, and emitting zero lets the visualizer
/// "breathe down" during quiet passages (B3 acceptance: visual gets
/// "дыхание" from band_energy.low).</para>
/// </summary>
public sealed class BandEnergyDetector : IContinuousDetector
{
    /// <summary>Band definitions: half-open bin intervals derived per-frame.</summary>
    public static readonly (string Key, float LoHz, float HiHz)[] Bands =
    {
        ("band_energy.low",   20f,   250f),   // sub-bass + bass + low mid
        ("band_energy.mid",  250f,  2000f),   // melody / instruments / vocals
        ("band_energy.high", 2000f, 8000f),   // presence / air / cymbals
    };

    public void Reset() { /* stateless */ }

    public void ProcessContinuous(SpectralFrame frame, EventSink sink)
    {
        var mags = frame.Magnitudes;
        if (mags.Length == 0) return;

        // Total non-DC energy. Skip bin 0 (DC): it carries no audible content
        // and would bias fractions on tracks with DC offset.
        double totalEnergy = 0.0;
        for (int i = 1; i < mags.Length; i++)
        {
            double m = mags[i];
            totalEnergy += m * m;
        }

        if (totalEnergy <= 0.0)
        {
            // True silence — emit zeros so visual decays.
            foreach (var (key, _, _) in Bands)
                sink.EmitContinuous(key, 0f);
            return;
        }

        double binHz = (double)frame.SampleRate / frame.FftSize;

        foreach (var (key, loHz, hiHz) in Bands)
        {
            // Half-open bin interval [loBin, hiBinExcl). Boundary bins go
            // to exactly one band — tested in BoundaryBin_CountedOnce.
            int loBin = Math.Max(1, (int)Math.Floor(loHz / binHz));
            int hiBinExcl = Math.Min(mags.Length, (int)Math.Ceiling(hiHz / binHz));

            if (hiBinExcl <= loBin)
            {
                sink.EmitContinuous(key, 0f);
                continue;
            }

            double bandEnergy = 0.0;
            for (int i = loBin; i < hiBinExcl; i++)
            {
                double m = mags[i];
                bandEnergy += m * m;
            }

            sink.EmitContinuous(key, (float)(bandEnergy / totalEnergy));
        }
    }
}
