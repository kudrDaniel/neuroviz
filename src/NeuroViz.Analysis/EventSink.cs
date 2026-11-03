using System.Collections.Concurrent;
using NeuroViz.Core.Events;

namespace NeuroViz.Analysis;

public sealed record AnalyzedEvent(AudioEvent Event, SpectralFrame? Frame);

public sealed class EventSink
{
    private readonly ConcurrentQueue<AnalyzedEvent> _queue = new();
    private Dictionary<string, float> _continuous = new();
    private readonly object _contLock = new();

    public event Action<AnalyzedEvent>? EventEmitted;

    public void Emit(AudioEvent ev, SpectralFrame? frame)
    {
        var analyzed = new AnalyzedEvent(ev, frame);
        _queue.Enqueue(analyzed);
        EventEmitted?.Invoke(analyzed);
    }

    public bool TryDequeue(out AnalyzedEvent? analyzed) => _queue.TryDequeue(out analyzed);

    public IReadOnlyList<AnalyzedEvent> DrainAll()
    {
        var list = new List<AnalyzedEvent>();
        while (_queue.TryDequeue(out var ev)) list.Add(ev);
        return list;
    }

    /// <summary>
    /// Emits a continuous value for the current tick. Overwrites any prior
    /// value for the same key since the last drain. Called from the analyzer
    /// thread only; UI reads via <see cref="DrainContinuous"/>.
    /// </summary>
    public void EmitContinuous(string key, float value)
    {
        lock (_contLock) { _continuous[key] = value; }
    }

    /// <summary>
    /// Atomically swaps out accumulated continuous values. Returns an empty
    /// dictionary (never null) if no detector emitted since the last drain.
    /// </summary>
    public IReadOnlyDictionary<string, float> DrainContinuous()
    {
        lock (_contLock)
        {
            var snapshot = _continuous;
            _continuous = new();
            return snapshot;
        }
    }
}
