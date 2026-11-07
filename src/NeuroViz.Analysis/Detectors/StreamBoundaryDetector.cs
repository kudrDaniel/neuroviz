using NeuroViz.Core.Events;

namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Sticky section-boundary state driven by novelty events. Emits
/// <c>stream_boundary</c> with <c>params.open = 1</c> when a section
/// starts, <c>= 0</c> when it ends.
///
/// <para><b>Dual role:</b> implements <see cref="IEventDetector"/> for
/// per-frame timeout close, and <see cref="IEventObserver"/> to observe
/// novelty events for open/refresh. Both roles share the same instance;
/// register it on the pipeline once as a detector and once as an
/// observer.</para>
///
/// <para><b>Open rule:</b> two "strong" novelty events within
/// <c>openWindowSeconds</c> (4 s default) open the stream. Two, not one —
/// a single drum fill is not a section change. Strong = confidence ≥ 0.75
/// OR error_magnitude ≥ 4.0; either signal fires.</para>
///
/// <para><b>Refresh rule:</b> while open, any strong novelty updates
/// <c>lastStrongNoveltySeconds</c> — the close timer resets, no extra
/// event emitted.</para>
///
/// <para><b>Close rule:</b> 8 s of silence (no strong novelty) closes
/// the stream. Checked in <see cref="Process"/> on every frame.</para>
///
/// <para><b>Refractory:</b> 2 s between consecutive open events —
/// prevents rapid open/close flapping on noisy material.</para>
///
/// <para><b>Persistence:</b> unlike continuous metrics (KI-2), stream
/// boundary is an AudioEvent and is saved in .nvproj (it's in
/// Timeline.Events). On load, the boundary events are present as data,
/// but the sticky state machine starts fresh (closed) — replay of a
/// loaded project doesn't drive MainNode visuals from boundary until
/// C4 rewires it.</para>
/// </summary>
public sealed class StreamBoundaryDetector
    : IEventDetector, IEventObserver, ISeekAwareDetector
{
    private readonly double _openWindowSeconds;
    private readonly int _openRequiredCount;
    private readonly double _closeAfterSeconds;
    private readonly double _refractorySeconds;
    private readonly float _strongConfidence;
    private readonly float _strongMagnitude;
    private readonly double _maxOpenSeconds;

    private readonly Queue<double> _recentStrongTimestamps = new();
    private bool _isOpen;
    private double _lastStrongNoveltySeconds = double.NegativeInfinity;
    private double _lastBoundarySeconds = double.NegativeInfinity;
    private double _openStartedSeconds = double.NegativeInfinity;

    private float _lastStrongMagnitude;

    public StreamBoundaryDetector(
        double openWindowSeconds = 4.0,
        int openRequiredCount = 2,
        double closeAfterSeconds = 5.0,
        double refractorySeconds = 2.0,
        float strongConfidence = 0.75f,
        float strongMagnitude = 4.0f,
        double maxOpenSeconds = 30.0)
    {
        if (openRequiredCount < 1)
            throw new ArgumentOutOfRangeException(nameof(openRequiredCount));
        if (openWindowSeconds <= 0 || closeAfterSeconds <= 0 || refractorySeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(openWindowSeconds));

        _openWindowSeconds = openWindowSeconds;
        _openRequiredCount = openRequiredCount;
        _closeAfterSeconds = closeAfterSeconds;
        _refractorySeconds = refractorySeconds;
        _strongConfidence = strongConfidence;
        _strongMagnitude = strongMagnitude;
        _maxOpenSeconds = maxOpenSeconds;
    }

    public void Reset()
    {
        _recentStrongTimestamps.Clear();
        _isOpen = false;
        _lastStrongNoveltySeconds = double.NegativeInfinity;
        _lastBoundarySeconds = double.NegativeInfinity;
        _lastStrongMagnitude = 0f;
        _openStartedSeconds = double.NegativeInfinity;
    }

    // ── IEventDetector: per-frame timeout close ─────────────────────
    public IEnumerable<AudioEvent> Process(SpectralFrame frame)
    {
        if (!_isOpen) yield break;

        // Force close: a chapter that has been open too long is no longer
        // useful as a chapter marker — close it and let the next two strong
        // novelties open a fresh one. Without this, dense tracks never close.
        if (frame.TimestampSeconds - _openStartedSeconds >= _maxOpenSeconds)
        {
            _isOpen = false;
            _recentStrongTimestamps.Clear();
            _lastBoundarySeconds = frame.TimestampSeconds;
            yield return BoundaryEvent(frame.TimestampSeconds, open: false);
            yield break;
        }

        if (frame.TimestampSeconds - _lastStrongNoveltySeconds < _closeAfterSeconds)
            yield break;

        _isOpen = false;
        _recentStrongTimestamps.Clear();
        _lastBoundarySeconds = frame.TimestampSeconds;
        yield return BoundaryEvent(frame.TimestampSeconds, open: false);
    }

    // ── IEventObserver: novelty-driven open / refresh ───────────────
    public IEnumerable<AudioEvent> OnEventEmitted(AudioEvent ev)
    {
        if (ev.Type != EventTypes.Novelty) yield break;

        var isStrong = ev.Confidence >= _strongConfidence
                    || ev.Params.GetValueOrDefault("error_magnitude") >= _strongMagnitude;
        if (!isStrong) yield break;

        _lastStrongNoveltySeconds = ev.TimestampSeconds;

        _lastStrongMagnitude = ev.Params.GetValueOrDefault("error_magnitude");

        // Already open: strong novelty just refreshed the timeout above.
        if (_isOpen) yield break;

        // Windowed count: prune stale timestamps, enqueue current.
        // A candidate is valid only if it is BOTH within the open window AND
        // after the refractory period ended — otherwise the moment refractory
        // elapses, stale evidence from the refractory period would open the
        // stream on the first fresh novelty, defeating the debounce.
        var windowCutoff = ev.TimestampSeconds - _openWindowSeconds;
        var refractoryEnd = _lastBoundarySeconds + _refractorySeconds;
        var cutoff = Math.Max(windowCutoff, refractoryEnd);
        while (_recentStrongTimestamps.Count > 0 && _recentStrongTimestamps.Peek() < cutoff)
            _recentStrongTimestamps.Dequeue();
        _recentStrongTimestamps.Enqueue(ev.TimestampSeconds);

        if (_recentStrongTimestamps.Count < _openRequiredCount) yield break;
        if (ev.TimestampSeconds - _lastBoundarySeconds < _refractorySeconds) yield break;

        _isOpen = true;
        _openStartedSeconds = ev.TimestampSeconds;
        _lastBoundarySeconds = ev.TimestampSeconds;
        _recentStrongTimestamps.Clear();
        yield return BoundaryEvent(ev.TimestampSeconds, open: true);
    }

    private AudioEvent BoundaryEvent(double t, bool open) => new()
    {
        Type = EventTypes.StreamBoundary,
        TimestampSeconds = t,
        Confidence = 1f,
        SourceLayer = 2,
        Params = new Dictionary<string, float>
        {
            ["open"] = open ? 1f : 0f,
            // N.7-prep: strength carries the magnitude of the last strong
            // novelty that contributed to this boundary. Downstream scenes
            // use it to scale the response — the neural visualizer uses it
            // to decide how many nodes move on section change (Q2).
            ["strength"] = open ? _lastStrongMagnitude : 0f,
        },
    };

    public void OnSeek(double oldPos, double newPos)
    {
        // Seek is a jump, not a temporal pause-rewind. Shifting the cached
        // timestamps by (newPos - oldPos) worked for close-timer semantics
        // ("time elapsed since last strong novelty in track time"), but it
        // left _isOpen in the wrong state: seeking backward while open
        // meant the detector thought it was still open, so the next
        // strong novelty at the same boundary was silently ignored
        // ("_isOpen → yield break") and open=1 never re-emitted. The
        // neural visualizer's restructure, driven by open=1 events, then
        // never fired on replay even though the network had been restored
        // to pre-boundary state by the snapshot.
        //
        // Reset everything. The detector behaves as if the track just
        // started at the new position: the next two strong novelties in
        // the window will open a fresh stream, as on the first pass.
        _recentStrongTimestamps.Clear();
        _isOpen = false;
        _lastStrongNoveltySeconds = double.NegativeInfinity;
        _lastBoundarySeconds = double.NegativeInfinity;
        _openStartedSeconds = double.NegativeInfinity;
    }
}
