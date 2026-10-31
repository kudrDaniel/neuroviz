using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class NoveltyDetector : IEventDetector
{
    private readonly float _alpha;
    private readonly int _historySize;
    private readonly float _thresholdMult;
    private readonly double _refractorySeconds;
    private readonly Queue<float> _errorHistory = new();

    private float[] _predictedMags = Array.Empty<float>();
    private float _prevError;
    private double _lastEventSeconds = double.NegativeInfinity;

    private readonly float _minVolume;

    public NoveltyDetector(
    float alpha = 0.01f,              // было 0.1f
    int historySize = 50,             // было 30
    float thresholdMult = 2.5f,       // было 2.0f
    double refractorySeconds = 1.5,   // было 0.4
    float minVolume = 0.5f)
    {
        _alpha = alpha;
        _historySize = historySize;
        _thresholdMult = thresholdMult;
        _refractorySeconds = refractorySeconds;
        _minVolume = minVolume;
    }

    public void Reset()
    {
        _errorHistory.Clear();
        _predictedMags = Array.Empty<float>();
        _prevError = 0f;
        _lastEventSeconds = double.NegativeInfinity;
    }

    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        var mags = frame.Magnitudes;

        if (_predictedMags.Length != mags.Length)
        {
            _predictedMags = (float[])mags.Clone();
            yield break;
        }

        float l2Error = 0f;
        float sumMag = 0f;
        float sumPredicted = 0f;

        for (int i = 0; i < mags.Length; i++)
        {
            float diff = mags[i] - _predictedMags[i];
            l2Error += diff * diff;
            sumMag += mags[i];
            sumPredicted += _predictedMags[i];

            _predictedMags[i] = _alpha * mags[i] + (1f - _alpha) * _predictedMags[i];
        }

        // 1. Порог тишины. Если в треке тихо, мы просто привыкаем к тишине.
        float rmsMag = sumMag / mags.Length;
        if (rmsMag < _minVolume)
        {
            _prevError = 0f;
            yield break;
        }

        // 2. Просто берем среднюю квадратичную ошибку.
        // Она уже адекватно отражает новизну (обычно значения от 1.0 до 20.0)
        float normalizedError = MathF.Sqrt(l2Error / mags.Length);

        if (_errorHistory.Count < _historySize)
        {
            _errorHistory.Enqueue(normalizedError);
            _prevError = normalizedError;
            yield break;
        }

        double mean = 0;
        foreach (var e in _errorHistory) mean += e;
        mean /= _errorHistory.Count;

        double sq = 0;
        foreach (var e in _errorHistory) sq += (e - mean) * (e - mean);
        var std = Math.Sqrt(sq / _errorHistory.Count);

        var threshold = (float)(mean + _thresholdMult * std);

        var refractoryElapsed = frame.TimestampSeconds - _lastEventSeconds >= _refractorySeconds;
        var isRisingEdge = normalizedError > threshold && normalizedError > _prevError && refractoryElapsed;

        _errorHistory.Enqueue(normalizedError);
        while (_errorHistory.Count > _historySize) _errorHistory.Dequeue();
        _prevError = normalizedError;

        if (isRisingEdge)
        {
            _lastEventSeconds = frame.TimestampSeconds;
            var conf = Math.Clamp((normalizedError - threshold) / (threshold + 1e-6f), 0f, 1f);

            yield return new AudioEvent
            {
                Type = EventTypes.Novelty,
                TimestampSeconds = frame.TimestampSeconds,
                Confidence = conf,
                SourceLayer = 2,
                Params = new Dictionary<string, float>
                {
                    ["error_magnitude"] = normalizedError,
                    ["threshold"] = threshold,
                },
            };
        }
    }
}