using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Mirror of <see cref="OnsetDetector"/> on the falling edge of spectral
/// flux. Detects the moment when a note / phrase ends: a sharp increase
/// in <c>decline</c> (magnitude of negative flux) means the spectrum is
/// collapsing.
///
/// <para><b>Design symmetry with OnsetDetector:</b> same historySize /
/// thresholdMult / refractorySeconds defaults, same isRisingEdge→isFallingEdge
/// shape, same PercentileConfidence model. Offset is NOT a re-classification
/// of onset events — a rising edge produces <c>decline = 0</c> and cannot
/// trigger this detector (see OffsetDetectorTests.RisingEdge_DoesNotFire).</para>
///
/// <para><b>Params:</b> <c>decline</c> (raw magnitude of negative flux in
/// the current frame) and <c>threshold</c> (the mean+std threshold at that
/// moment). Rules that want an absolute floor should filter on
/// <c>params.decline: { min: N }</c>; the basic offset-release rule uses
/// only <c>confidence</c>.</para>
/// </summary>
public sealed class OffsetDetector : IEventDetector
{
    private const float WarmupConfidence = 0.5f;

    private readonly int _historySize;
    private readonly float _thresholdMult;
    private readonly double _refractorySeconds;
    private readonly Queue<float> _declineHistory = new();
    private readonly PercentileConfidence _confidence;

    private float[] _prevMags = Array.Empty<float>();
    private float _prevDecline;
    private double _lastOffsetSeconds = double.NegativeInfinity;

    public OffsetDetector(
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

        // Same defaults as OnsetDetector (B1a): capacity = historySize,
        // warm-up = max(3, historySize/4).
        var confCap = confidenceCapacity ?? historySize;
        var confMin = confidenceMinHistory ?? Math.Max(3, historySize / 4);
        _confidence = new PercentileConfidence(confCap, confMin, WarmupConfidence);
    }

    public void Reset()
    {
        _declineHistory.Clear();
        _prevMags = Array.Empty<float>();
        _prevDecline = 0f;
        _lastOffsetSeconds = double.NegativeInfinity;
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

        float decline = 0f;
        for (int i = 0; i < mags.Length; i++)
        {
            var diff = mags[i] - _prevMags[i];
            if (diff < 0) decline += -diff;   // mirror of onset's "if (diff > 0) flux += diff"
        }
        Array.Copy(mags, _prevMags, mags.Length);

        // Warm-up: fill history, do not detect.
        if (_declineHistory.Count < _historySize)
        {
            _declineHistory.Enqueue(decline);
            _prevDecline = decline;
            yield break;
        }

        double mean = 0;
        foreach (var d in _declineHistory) mean += d;
        mean /= _declineHistory.Count;

        double sq = 0;
        foreach (var d in _declineHistory) sq += (d - mean) * (d - mean);
        var std = Math.Sqrt(sq / _declineHistory.Count);

        var threshold = (float)(mean + _thresholdMult * std);

        var refractoryElapsed = frame.TimestampSeconds - _lastOffsetSeconds >= _refractorySeconds;
        var isFallingEdge = decline > threshold && decline > _prevDecline && refractoryElapsed;

        _declineHistory.Enqueue(decline);
        while (_declineHistory.Count > _historySize) _declineHistory.Dequeue();
        _prevDecline = decline;

        if (isFallingEdge)
        {
            _lastOffsetSeconds = frame.TimestampSeconds;

            // Percentile of decline across recent offset events (B1a model).
            var conf = _confidence.Estimate(decline);
            _confidence.Observe(decline);

            yield return new AudioEvent
            {
                Type = EventTypes.Offset,
                TimestampSeconds = frame.TimestampSeconds,
                Confidence = conf,
                SourceLayer = 1,
                Params = new Dictionary<string, float>
                {
                    ["decline"] = decline,
                    ["threshold"] = threshold,
                },
            };
        }
    }
}
