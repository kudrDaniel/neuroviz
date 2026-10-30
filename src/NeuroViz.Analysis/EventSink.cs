using System.Collections.Concurrent;
using NeuroViz.Core.Events;

namespace NeuroViz.Analysis;

/// <summary>
/// Потокобезопасный выход пайплайна. Emit — из аудио-потока,
/// чтение (TryDequeue / DrainAll) — из UI-потока.
/// </summary>
public sealed class EventSink
{
    private readonly ConcurrentQueue<AudioEvent> _queue = new();

    public event Action<AudioEvent>? EventEmitted;

    public void Emit(AudioEvent ev)
    {
        _queue.Enqueue(ev);
        EventEmitted?.Invoke(ev);
    }

    public bool TryDequeue(out AudioEvent? ev) => _queue.TryDequeue(out ev);

    public IReadOnlyList<AudioEvent> DrainAll()
    {
        var list = new List<AudioEvent>();
        while (_queue.TryDequeue(out var ev)) list.Add(ev!);
        return list;
    }
}