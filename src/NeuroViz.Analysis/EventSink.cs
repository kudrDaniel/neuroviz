using System.Collections.Concurrent;
using NeuroViz.Core.Events;

namespace NeuroViz.Analysis;

public sealed record AnalyzedEvent(AudioEvent Event, SpectralFrame? Frame);

public sealed class EventSink
{
    private readonly ConcurrentQueue<AnalyzedEvent> _queue = new();

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
}