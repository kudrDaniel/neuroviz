using MessagePack;
using NeuroViz.Core.Events;

namespace NeuroViz.Core.Ipc;

[MessagePackObject]
public sealed class EventStreamFrame
{
    [Key(0)] public double AudioClockSeconds { get; init; }
    [Key(1)] public List<AudioEvent> Events { get; init; } = new();
    [Key(2)] public Dictionary<string, ParamValue> Params { get; init; } = new();
    [Key(3)] public long FrameSeq { get; init; }
}