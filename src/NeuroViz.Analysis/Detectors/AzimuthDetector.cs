using NeuroViz.Core.Events;
using NeuroViz.Audio;

namespace NeuroViz.Analysis.Detectors;

public sealed class AzimuthDetector : IPcmEventDetector
{
    private readonly double _refractorySeconds;
    private readonly PercentileConfidence _confidence;
    private double _lastEventSeconds = double.NegativeInfinity;

    public AzimuthDetector(
        double refractorySeconds = 0.05,
        int? confidenceCapacity = null,
        int? confidenceMinHistory = null)
    {
        _refractorySeconds = refractorySeconds;

        // Up to 20 events/s (refractory 0.05s). ~1s window of recent panning.
        _confidence = new PercentileConfidence(
            confidenceCapacity ?? 20,
            confidenceMinHistory ?? 5);
    }

    public void Reset()
    {
        _lastEventSeconds = double.NegativeInfinity;
        _confidence.Reset();
    }

    public IEnumerable<AudioEvent> Process(SpectralFrame frame) => Array.Empty<AudioEvent>();

    public IEnumerable<AudioEvent> ProcessPcm(PcmFrame frame)
    {
        if (frame.Channels < 2) yield break; // Только стерео
        if (frame.PositionSeconds - _lastEventSeconds < _refractorySeconds) yield break;

        var samples = frame.Samples;
        var half = samples.Length / 2;

        // Считаем RMS (громкость) для левого и правого каналов
        float sumL = 0, sumR = 0;
        for (int i = 0; i < half; i++)
        {
            sumL += samples[i * 2] * samples[i * 2];
            sumR += samples[i * 2 + 1] * samples[i * 2 + 1];
        }

        float rmsL = MathF.Sqrt(sumL / half);
        float rmsR = MathF.Sqrt(sumR / half);

        if (rmsL < 0.01f && rmsR < 0.01f) yield break; // Тишина

        // 1. ILD (Разница уровней в дБ) -> от -1 (лево) до 1 (право)
        float ildDb = 20f * MathF.Log10((rmsL + 1e-5f) / (rmsR + 1e-5f));
        float ildNorm = Math.Clamp(ildDb / 12f, -1f, 1f); // 12 дБ разницы = полная панорама

        // 2. ITD (Разница по времени через кросс-корреляцию)
        // Для простоты берем только ILD, так как ITD требует сложной свертки
        // В идеале здесь нужно делать кросс-корреляцию

        _lastEventSeconds = frame.PositionSeconds;

        // Semantic change from B1a's predecessor: previously |ildNorm| was
        // used directly as confidence. Now confidence is the percentile of
        // |ildNorm| across recent events — "how unusual is this panning
        // relative to recent material?" The raw ILD remains in
        // params.azimuth_norm; rules that want absolute magnitude should
        // read it via param_from_event (which is what basic.json's
        // azimuth-pan rule already does).
        float panMagnitude = MathF.Abs(ildNorm);
        var conf = _confidence.Estimate(panMagnitude);
        _confidence.Observe(panMagnitude);

        yield return new AudioEvent
        {
            Type = EventTypes.AzimuthEstimate,
            TimestampSeconds = frame.PositionSeconds,
            Confidence = conf,
            SourceLayer = 1,
            Params = new Dictionary<string, float>
            {
                ["azimuth_norm"] = ildNorm, // -1.0 (лево) ... 1.0 (право)
                ["rms"] = (rmsL + rmsR) / 2f
            }
        };
    }
}
