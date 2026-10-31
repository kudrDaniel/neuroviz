using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class SpectralFeaturesDetector : IEventDetector
{
    private readonly double _refractorySeconds;
    private double _lastEventSeconds = double.NegativeInfinity;

    public SpectralFeaturesDetector(double refractorySeconds = 0.05)
    {
        _refractorySeconds = refractorySeconds;
    }

    public void Reset() => _lastEventSeconds = double.NegativeInfinity;

    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        if (frame.TimestampSeconds - _lastEventSeconds < _refractorySeconds) yield break;

        var mags = frame.Magnitudes;
        if (mags.Length == 0) yield break;

        float sumMag = 0f;
        float sumWeighted = 0f;
        double sumLog = 0.0;
        int count = 0;

        for (int i = 1; i < mags.Length; i++) // Пропускаем DC-компоненту (i=0)
        {
            float mag = mags[i];
            sumMag += mag;

            // Для Spectral Centroid (взвешиваем номер бина на магнитуду)
            sumWeighted += i * mag;

            // Для Spectral Flatness (логарифм с защитой от нуля)
            sumLog += Math.Log(mag + 1e-10);
            count++;
        }

        if (sumMag < 1e-4f) yield break; // Тишина

        // 1. Spectral Centroid (в бинах)
        float centroidBin = sumWeighted / sumMag;
        // Нормируем (максимальный интересующий бин — примерно 1/4 от всех, т.е. до ~5 кГц)
        float centroidNorm = Math.Clamp(centroidBin / (mags.Length * 0.25f), 0f, 1f);

        // 2. Spectral Flatness (Тональность)
        // Flatness = Geometric Mean / Arithmetic Mean
        double geometricMean = Math.Exp(sumLog / count);
        double arithmeticMean = sumMag / count;
        float flatness = (float)(geometricMean / arithmeticMean);

        // Flatness близок к 1 для шума и к 0 для чистых тонов.
        // Инвертируем, чтобы получить "Harmonicity" (1 = чистый тон, 0 = шум)
        float harmonicity = 1f - Math.Clamp(flatness * 10f, 0f, 1f);

        _lastEventSeconds = frame.TimestampSeconds;

        // Выдаем событие центроида
        yield return new AudioEvent
        {
            Type = EventTypes.SpectralCentroid,
            TimestampSeconds = frame.TimestampSeconds,
            Confidence = 1f,
            SourceLayer = 1,
            Params = new Dictionary<string, float> { ["centroid_norm"] = centroidNorm }
        };

        // Выдаем событие гармоничности
        yield return new AudioEvent
        {
            Type = EventTypes.Harmonicity,
            TimestampSeconds = frame.TimestampSeconds,
            Confidence = 1f,
            SourceLayer = 1,
            Params = new Dictionary<string, float> { ["harmonicity"] = harmonicity }
        };
    }
}