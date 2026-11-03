namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Tracks strengths (e.g. flux, novelty error) of the last N events a detector
/// has emitted, and estimates confidence as the mid-rank percentile of a new
/// candidate among them.
///
/// <para><b>Why mid-rank percentile:</b> the old variance-based formula
/// <c>(signal - threshold) / threshold</c> saturates to 1.0 on quiet or sparse
/// material because <c>threshold</c> itself collapses to near-zero when most
/// frames have near-zero flux — any real hit is a massive outlier.
/// Comparing events to events (rather than events to frames) yields a
/// confidence that stays meaningful on both loud and quiet tracks. See
/// docs/KNOWN_ISSUES.md (KI-1).</para>
///
/// <para><b>Mid-rank:</b> ties contribute half. Two events with identical
/// strength both get 0.5*tied/N added to their rank, so uniform material
/// yields ~0.5 confidence for all events rather than 0 or 1.</para>
///
/// <para><b>Warm-up:</b> until <see cref="MinHistory"/> events have been
/// observed, <see cref="Estimate"/> returns <see cref="WarmupFallback"/>
/// (0.5 by default) because there is no distribution to compare against.
/// This is honest — a confidence of 0.5 means "we cannot tell yet", while
/// the old formula falsely reported 1.0 during warm-up on quiet material.</para>
/// </summary>
public sealed class PercentileConfidence
{
    private readonly int _capacity;
    private readonly Queue<float> _history;

    public PercentileConfidence(
        int capacity = 20,
        int minHistory = 5,
        float warmupFallback = 0.5f)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be >= 1");
        if (minHistory < 1 || minHistory > capacity)
            throw new ArgumentOutOfRangeException(nameof(minHistory),
                "must be in [1, capacity]");
        if (warmupFallback < 0f || warmupFallback > 1f)
            throw new ArgumentOutOfRangeException(nameof(warmupFallback),
                "must be in [0, 1]");

        _capacity = capacity;
        MinHistory = minHistory;
        WarmupFallback = warmupFallback;
        _history = new Queue<float>(capacity);
    }

    public int MinHistory { get; }
    public float WarmupFallback { get; }
    public int Count => _history.Count;
    public bool IsWarmedUp => _history.Count >= MinHistory;

    public void Reset() => _history.Clear();

    /// <summary>
    /// Confidence for a candidate event of <paramref name="strength"/>,
    /// based on the distribution of recent event strengths. Returns
    /// <see cref="WarmupFallback"/> until warmed up.
    /// </summary>
    public float Estimate(float strength)
    {
        if (_history.Count < MinHistory)
            return WarmupFallback;

        int below = 0;
        int equal = 0;
        foreach (var h in _history)
        {
            if (h < strength) below++;
            else if (h == strength) equal++;
        }

        // Mid-rank: (strictly below + 0.5 * tied) / count.
        //   highest unique event → 1.0
        //   lowest unique event  → 0.0
        //   all-equal            → 0.5
        return (below + 0.5f * equal) / _history.Count;
    }

    /// <summary>
    /// Register an event strength. Call <see cref="Estimate"/> <b>before</b>
    /// this method for the same event, so the current event doesn't pollute
    /// its own comparison basis.
    /// </summary>
    public void Observe(float strength)
    {
        if (_history.Count >= _capacity) _history.Dequeue();
        _history.Enqueue(strength);
    }
}
