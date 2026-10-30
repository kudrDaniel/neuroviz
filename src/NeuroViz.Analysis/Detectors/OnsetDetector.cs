using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class OnsetDetector : IEventDetector
{
    private readonly int _historySize;
    private readonly float _thresholdMult;
    private readonly double _refractorySeconds;
    private readonly Queue<float> _fluxHistory = new();

    private float[] _prevMags = Array.Empty<float>();
    private float _prevFlux;
    private double _lastOnsetSeconds = double.NegativeInfinity;

    public OnsetDetector(
        int historySize = 20,
        float thresholdMult = 1.5f,
        double refractorySeconds = 0.03)
    {
        if (historySize < 2) throw new ArgumentOutOfRangeException(nameof(historySize));
        _historySize = historySize;
        _thresholdMult = thresholdMult;
        _refractorySeconds = refractorySeconds;
    }

    public void Reset()
    {
        _fluxHistory.Clear();
        _prevMags = Array.Empty<float>();
        _prevFlux = 0f;
        _lastOnsetSeconds = double.NegativeInfinity;
    }

    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        var mags = frame.Magnitudes;

        if (_prevMags.Length != mags.Length)
        {
            _prevMags = new float[mags.Length];
            Array.Copy(mags, _prevMags, mags.Length);
            yield break;
        }

        float flux = 0f;
        for (int i = 0; i < mags.Length; i++)
        {
            var diff = mags[i] - _prevMags[i];
            if (diff > 0) flux += diff;
        }
        Array.Copy(mags, _prevMags, mags.Length);

        // Warm-up: наполняем историю, ничего не детектируем.
        if (_fluxHistory.Count < _historySize)
        {
            _fluxHistory.Enqueue(flux);
            _prevFlux = flux;
            yield break;
        }

        // Статистика по ПРОШЛЫМ кадрам (без текущего).
        double mean = 0;
        foreach (var f in _fluxHistory) mean += f;
        mean /= _fluxHistory.Count;

        double sq = 0;
        foreach (var f in _fluxHistory) sq += (f - mean) * (f - mean);
        var std = Math.Sqrt(sq / _fluxHistory.Count);

        var threshold = (float)(mean + _thresholdMult * std);

        var refractoryElapsed = frame.TimestampSeconds - _lastOnsetSeconds >= _refractorySeconds;
        var isRisingEdge = flux > threshold && flux > _prevFlux && refractoryElapsed;

        // Обновляем историю ПОСЛЕ проверки.
        _fluxHistory.Enqueue(flux);
        while (_fluxHistory.Count > _historySize) _fluxHistory.Dequeue();
        _prevFlux = flux;

        if (isRisingEdge)
        {
            _lastOnsetSeconds = frame.TimestampSeconds;
            var conf = Math.Clamp((flux - threshold) / (threshold + 1e-6f), 0f, 1f);

            yield return new AudioEvent
            {
                Type = EventTypes.Onset,
                TimestampSeconds = frame.TimestampSeconds,
                Confidence = conf,
                SourceLayer = 1,
                Params = new Dictionary<string, float>
                {
                    ["flux"] = flux,
                    ["threshold"] = threshold,
                },
            };
        }
    }
}