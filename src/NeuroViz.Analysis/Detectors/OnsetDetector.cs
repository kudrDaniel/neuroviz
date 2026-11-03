using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

public sealed class OnsetDetector : IEventDetector
{
    private const float WarmupConfidence = 0.5f;

    private readonly int _historySize;
    private readonly float _thresholdMult;
    private readonly double _refractorySeconds;
    private readonly Queue<float> _fluxHistory = new();
    private readonly PercentileConfidence _confidence;

    private float[] _prevMags = Array.Empty<float>();
    private float _prevFlux;
    private double _lastOnsetSeconds = double.NegativeInfinity;

    public OnsetDetector(
        int historySize = 20,
        float thresholdMult = 2.2f,
        double refractorySeconds = 0.07,
        int? confidenceCapacity = null,
        int? confidenceMinHistory = null)
    {
        if (historySize < 2) throw new ArgumentOutOfRangeException(nameof(historySize));
        _historySize = historySize;
        _thresholdMult = thresholdMult;
        _refractorySeconds = refractorySeconds;

        // Defaults scale with the detector's frame window: a 20-frame onset
        // window already provides a 20-event confidence window; warm-up at ¼
        // of the window (min 3) keeps first events from being classified as
        // outliers before a distribution exists.
        var confCap = confidenceCapacity ?? historySize;
        var confMin = confidenceMinHistory ?? Math.Max(3, historySize / 4);
        _confidence = new PercentileConfidence(confCap, confMin, WarmupConfidence);
    }

    public void Reset()
    {
        _fluxHistory.Clear();
        _prevMags = Array.Empty<float>();
        _prevFlux = 0f;
        _lastOnsetSeconds = double.NegativeInfinity;
        _confidence.Reset();
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

        // Warm-up: fill history, do not detect.
        if (_fluxHistory.Count < _historySize)
        {
            _fluxHistory.Enqueue(flux);
            _prevFlux = flux;
            yield break;
        }

        // Statistics over PAST frames (excluding current).
        double mean = 0;
        foreach (var f in _fluxHistory) mean += f;
        mean /= _fluxHistory.Count;

        double sq = 0;
        foreach (var f in _fluxHistory) sq += (f - mean) * (f - mean);
        var std = Math.Sqrt(sq / _fluxHistory.Count);

        var threshold = (float)(mean + _thresholdMult * std);

        var refractoryElapsed = frame.TimestampSeconds - _lastOnsetSeconds >= _refractorySeconds;
        var isRisingEdge = flux > threshold && flux > _prevFlux && refractoryElapsed;

        _fluxHistory.Enqueue(flux);
        while (_fluxHistory.Count > _historySize) _fluxHistory.Dequeue();
        _prevFlux = flux;

        if (isRisingEdge)
        {
            _lastOnsetSeconds = frame.TimestampSeconds;

            // Percentile-based confidence among recent *events*.
            // Estimate BEFORE Observe so the current event doesn't bias
            // its own rank. Warm-up (<MinHistory events) returns 0.5 —
            // honest about lacking data, instead of the old variance-based
            // formula that saturated to 1.0 on quiet material (KI-1).
            var conf = _confidence.Estimate(flux);
            _confidence.Observe(flux);

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
