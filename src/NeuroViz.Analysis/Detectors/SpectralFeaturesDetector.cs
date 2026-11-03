namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Per-frame spectral features, emitted as continuous values:
/// <list type="bullet">
///   <item><c>spectral_centroid</c> — normalized 0..1 (brightness).</item>
///   <item><c>harmonicity</c> — 0 = noise, 1 = pure tone.</item>
/// </list>
///
/// <para><b>Was</b> an IEventDetector that emitted two AudioEvents per frame
/// with <c>Confidence = 1f</c> hardcoded. That was semantically wrong (KI-1
/// variant B): centroid and harmonicity are continuous metrics, not discrete
/// events — there is no "event" to be confident about. Rules that filter on
/// <c>confidence</c> for these types had no meaning; rules that used
/// <c>param_from_event</c> were re-implemented directly in MainNode.</para>
///
/// <para>Emitted at STFT frame rate (~40 Hz). UI drains at ~30 Hz, so the
/// visualizer sees the freshest value each tick.</para>
/// </summary>
public sealed class SpectralFeaturesDetector : IContinuousDetector
{
    public void Reset() { /* stateless */ }

    public void ProcessContinuous(SpectralFrame frame, EventSink sink)
    {
        var mags = frame.Magnitudes;
        if (mags.Length == 0) return;

        float sumMag = 0f;
        float sumWeighted = 0f;
        double sumLog = 0.0;
        int count = 0;

        for (int i = 1; i < mags.Length; i++) // skip DC
        {
            float mag = mags[i];
            sumMag += mag;
            sumWeighted += i * mag;
            sumLog += Math.Log(mag + 1e-10);
            count++;
        }

        if (sumMag < 1e-4f) return; // silence: emit nothing (MainNode holds last)

        float centroidBin = sumWeighted / sumMag;
        float centroidNorm = Math.Clamp(centroidBin / (mags.Length * 0.25f), 0f, 1f);

        double geometricMean = Math.Exp(sumLog / count);
        double arithmeticMean = sumMag / count;
        float flatness = (float)(geometricMean / arithmeticMean);
        float harmonicity = 1f - Math.Clamp(flatness * 10f, 0f, 1f);

        sink.EmitContinuous("spectral_centroid", centroidNorm);
        sink.EmitContinuous("harmonicity", harmonicity);
    }
}
