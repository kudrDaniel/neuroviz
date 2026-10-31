using NeuroViz.Core.Events;
using NeuroViz.Audio;

namespace NeuroViz.Analysis.Detectors;

public sealed class AzimuthDetector : IPcmEventDetector
{
    private readonly double _refractorySeconds;
    private double _lastEventSeconds = double.NegativeInfinity;

    public AzimuthDetector(double refractorySeconds = 0.05)
    {
        _refractorySeconds = refractorySeconds;
    }

    public void Reset() => _lastEventSeconds = double.NegativeInfinity;
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

        // Выдаем событие панорамы
        yield return new AudioEvent
        {
            Type = EventTypes.AzimuthEstimate,
            TimestampSeconds = frame.PositionSeconds,
            Confidence = MathF.Abs(ildNorm), // Уверенность = насколько далеко от центра
            SourceLayer = 1,
            Params = new Dictionary<string, float>
            {
                ["azimuth_norm"] = ildNorm, // -1.0 (лево) ... 1.0 (право)
                ["rms"] = (rmsL + rmsR) / 2f
            }
        };
    }
}