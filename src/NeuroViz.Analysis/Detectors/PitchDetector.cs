using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class PitchDetector : IEventDetector
{
    private readonly double _refractorySeconds;
    private double _lastEventSeconds = double.NegativeInfinity;

    public PitchDetector(double refractorySeconds = 0.05)
    {
        _refractorySeconds = refractorySeconds;
    }

    public void Reset() => _lastEventSeconds = double.NegativeInfinity;

    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        if (frame.TimestampSeconds - _lastEventSeconds < _refractorySeconds) yield break;

        var mags = frame.Magnitudes;
        int half = mags.Length / 2;

        // HPS: перемножаем спектр с его сжатыми версиями
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

        // Если сигнал слишком тихий или не нашли пик — пропускаем
        if (bestBin <= 0 || maxHps < 1.0f) yield break;

        // Переводим bin в частоту (Гц)
        double freqHz = bestBin * (double)frame.SampleRate / frame.FftSize;

        // Отсекаем нереалистичные частоты (ниже 50 Гц и выше 4000 Гц)
        if (freqHz < 50 || freqHz > 4000) yield break;

        _lastEventSeconds = frame.TimestampSeconds;

        // Нормируем частоту в диапазон 0..1 (для удобства визуализации)
        float normPitch = (float)((freqHz - 50) / 3950.0);

        yield return new AudioEvent
        {
            Type = EventTypes.PitchSalience,
            TimestampSeconds = frame.TimestampSeconds,
            Confidence = Math.Clamp(maxHps / (maxHps + 10f), 0f, 1f),
            SourceLayer = 1,
            Params = new Dictionary<string, float>
            {
                ["frequency_hz"] = (float)freqHz,
                ["pitch_norm"] = normPitch, // 0.0 (низко) ... 1.0 (высоко)
                ["hps_strength"] = maxHps
            }
        };
    }
}