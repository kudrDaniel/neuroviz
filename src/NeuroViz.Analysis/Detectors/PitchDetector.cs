using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class PitchDetector : IEventDetector
{
    private readonly double _refractorySeconds;
    private readonly PercentileConfidence _confidence;
    private double _lastEventSeconds = double.NegativeInfinity;

    public PitchDetector(
        double refractorySeconds = 0.05,
        int? confidenceCapacity = null,
        int? confidenceMinHistory = null)
    {
        _refractorySeconds = refractorySeconds;

        // Pitch events fire up to 20/s (refractory 0.05s). A 20-event window
        // covers ~1 second of recent pitch material; warm-up 5 events.
        _confidence = new PercentileConfidence(
            confidenceCapacity ?? 20,
            confidenceMinHistory ?? 5);
    }

    public void Reset()
    {
        _lastEventSeconds = double.NegativeInfinity;
        _confidence.Reset();
    }

    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        if (frame.TimestampSeconds - _lastEventSeconds < _refractorySeconds) yield break;

        var mags = frame.Magnitudes;
        int half = mags.Length / 2;

        float maxHps = 0;
        int bestBin = -1;

        for (int i = 2; i < half; i++)
        {
            float hps = mags[i];
            if (i * 2 < mags.Length) hps *= mags[i * 2];
            if (i * 3 < mags.Length) hps *= mags[i * 3];
            if (i * 4 < mags.Length) hps *= mags[i * 4];

            if (hps > maxHps)
            {
                maxHps = hps;
                bestBin = i;
            }
        }

        if (bestBin <= 0 || maxHps < 1.0f) yield break;

        double freqHz = bestBin * (double)frame.SampleRate / frame.FftSize;
        if (freqHz < 50 || freqHz > 4000) yield break;

        _lastEventSeconds = frame.TimestampSeconds;

        float normPitch = (float)((freqHz - 50) / 3950.0);

        // Percentile of maxHps across recent pitch events. On uniform clean
        // material (all similar maxHps) every event ranks ~0.5 — no event
        // stands out, which is honest: if all notes are equally clear, none
        // is "interesting" for the confidence filter. On mixed material
        // (soft vs loud, clean vs noisy) clean/loud notes rank high.
        // Raw maxHps remains in params.hps_strength (KI-1).
        var conf = _confidence.Estimate(maxHps);
        _confidence.Observe(maxHps);

        yield return new AudioEvent
        {
            Type = EventTypes.PitchSalience,
            TimestampSeconds = frame.TimestampSeconds,
            Confidence = conf,
            SourceLayer = 1,
            Params = new Dictionary<string, float>
            {
                ["frequency_hz"] = (float)freqHz,
                ["pitch_norm"] = normPitch,
                ["hps_strength"] = maxHps
            }
        };
    }
}
