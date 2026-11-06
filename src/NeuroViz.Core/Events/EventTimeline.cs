using MessagePack;

namespace NeuroViz.Core.Events;

[MessagePackObject]
public sealed class EventTimeline
{
    [Key(0)] public int SchemaVersion { get; init; } = 3;
    [Key(1)] public string AudioPath { get; init; } = "";
    [Key(2)] public string AudioSha256 { get; init; } = "";
    [Key(3)] public double DurationSeconds { get; init; }
    [Key(4)] public List<AudioEvent> Events { get; init; } = new();
    [Key(5)] public Dictionary<string, string> AnalysisMeta { get; init; } = new();

    /// <summary>
    /// Continuous-metric samples, one per drain tick. Empty for v2 and
    /// older projects; populated by Re-analyze and by live playback
    /// recording (Save picks up whatever has been collected).
    /// </summary>
    [Key(6)] public List<ContinuousFrame> ContinuousFrames { get; init; } = new();
}
