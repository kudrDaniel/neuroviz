namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Per-frame rising spectral flux as a continuous value in [0, 1].
/// <c>flux = Σ max(0, mags[i] - prevMags[i]) / Σ mags[i]</c> — the
/// fraction of the current frame's total magnitude that represents
/// rising change since the previous frame.
///
/// <para><b>Why relative, not raw:</b> raw flux (sum of positive diffs)
/// scales with loudness — a quiet attack and a loud attack produce
/// wildly different raw values, making a single visual mapping
/// impossible. Relative flux captures <i>how fast the spectrum is
/// changing</i> independently of volume: a sustained note → ~0, a
/// sharp attack → ~1, a smooth swell → ~0.3. This is the metric that
/// maps naturally to "flash on attack, steady on sustain".</para>
///
/// <para><b>OnsetDetector keeps its own raw-flux copy.</b> Isolating
/// detectors from each other means: (a) no ordering constraint in the
/// pipeline, (b) OnsetDetector's threshold logic remains trivially
/// testable in isolation, (c) a future change to relative-flux
/// semantics cannot silently shift onset detection sensitivity.</para>
///
/// <para><b>Silence:</b> emits 0. A silent frame has no flux by
/// definition; emitting 0 lets the visualizer dim during quiet
/// passages rather than freezing on the last active value.</para>
///
/// <para><b>Warm-up:</b> the first frame has no previous spectrum, so
/// there is no flux to measure. Emits 0 (baseline, no flash) and
/// stores the frame for comparison.</para>
/// </summary>
public sealed class SpectralFluxDetector : IContinuousDetector
{
    private float[] _prevMags = Array.Empty<float>();

    public void Reset() => _prevMags = Array.Empty<float>();

    public void ProcessContinuous(SpectralFrame frame, EventSink sink)
    {
        var mags = frame.Magnitudes;
        if (mags.Length == 0) return;

        if (_prevMags.Length != mags.Length)
        {
            _prevMags = new float[mags.Length];
            Array.Copy(mags, _prevMags, mags.Length);
            sink.EmitContinuous("spectral_flux", 0f);
            return;
        }

        float totalMag = 0f;
        float risingFlux = 0f;
        for (int i = 0; i < mags.Length; i++)
        {
            totalMag += mags[i];
            var diff = mags[i] - _prevMags[i];
            if (diff > 0f) risingFlux += diff;
        }
        Array.Copy(mags, _prevMags, mags.Length);

        if (totalMag < 1e-4f)
        {
            sink.EmitContinuous("spectral_flux", 0f);
            return;
        }

        // risingFlux <= totalMag (since max(0, curr-prev) <= curr), so
        // relativeFlux is in [0, 1] by construction. Clamp guards against
        // float drift when curr ≈ prev on many bins.
        float relativeFlux = Math.Clamp(risingFlux / totalMag, 0f, 1f);
        sink.EmitContinuous("spectral_flux", relativeFlux);
    }
}
