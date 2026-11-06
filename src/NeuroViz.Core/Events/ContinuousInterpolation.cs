namespace NeuroViz.Core.Events;

/// <summary>
/// Reconstructs continuous params for an arbitrary playback position from
/// a sparse, timestamped frame list. Linear interpolation between adjacent
/// frames; clamps to first/last frame outside the sampled range.
///
/// Binary search — O(log N) per call, called ~30 Hz from OnTimerTick.
/// </summary>
public static class ContinuousInterpolation
{
    private static readonly IReadOnlyDictionary<string, float> Empty =
        new Dictionary<string, float>();

    public static IReadOnlyDictionary<string, float> EvaluateAt(
        IReadOnlyList<ContinuousFrame> frames, double pos)
    {
        if (frames.Count == 0) return Empty;

        // Clamp outside sampled range.
        if (pos <= frames[0].TimestampSeconds)
            return ToDict(frames[0]);
        if (pos >= frames[frames.Count - 1].TimestampSeconds)
            return ToDict(frames[frames.Count - 1]);

        // Binary search for [lo, hi] such that lo.T <= pos < hi.T.
        int lo = 0, hi = frames.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (frames[mid].TimestampSeconds <= pos) lo = mid;
            else hi = mid;
        }

        var a = frames[lo];
        var b = frames[hi];
        var span = b.TimestampSeconds - a.TimestampSeconds;
        var t = span > 1e-9 ? (pos - a.TimestampSeconds) / span : 0.0;

        var dict = new Dictionary<string, float>(ContinuousKeys.Count);
        var len = Math.Min(
            Math.Min(a.Values.Length, b.Values.Length),
            ContinuousKeys.Count);
        for (int i = 0; i < len; i++)
        {
            var v = a.Values[i] + (b.Values[i] - a.Values[i]) * (float)t;
            dict[ContinuousKeys.Ordered[i]] = v;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, float> ToDict(ContinuousFrame frame)
    {
        var dict = new Dictionary<string, float>(ContinuousKeys.Count);
        var len = Math.Min(frame.Values.Length, ContinuousKeys.Count);
        for (int i = 0; i < len; i++)
            dict[ContinuousKeys.Ordered[i]] = frame.Values[i];
        return dict;
    }
}
