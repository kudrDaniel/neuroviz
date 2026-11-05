using MessagePack;

namespace NeuroViz.Core.Events;

[MessagePackObject]
public sealed class EventTimeline
{
    [Key(0)] public int SchemaVersion { get; init; } = 2;
    [Key(1)] public string AudioPath { get; init; } = "";
    [Key(2)] public string AudioSha256 { get; init; } = "";
    [Key(3)] public double DurationSeconds { get; init; }
    [Key(4)] public List<AudioEvent> Events { get; init; } = new();
    [Key(5)] public Dictionary<string, string> AnalysisMeta { get; init; } = new();
}
