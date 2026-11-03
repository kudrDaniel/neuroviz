using MessagePack;

namespace NeuroViz.Core.Events;

[MessagePackObject]
public sealed class AudioEvent
{
    [Key(0)] public string Type { get; init; } = "";
    [Key(1)] public double TimestampSeconds { get; init; }

    /// <summary>
    /// Per-detector confidence in [0, 1]. Semantics: relative strength of
    /// this event compared to recent events from the <b>same detector</b>
    /// on the <b>same track</b>. NOT comparable across detectors or
    /// tracks — a <c>confidence=0.8</c> onset on quiet piano does not
    /// mean the same as <c>confidence=0.8</c> novelty on electronic music.
    /// Filters that threshold on confidence must be tuned per-detector
    /// (see TUNING.md).
    /// </summary>
    [Key(2)] public float Confidence { get; init; }

    [Key(3)] public Dictionary<string, float> Params { get; init; } = new();
    [Key(4)] public int SchemaVersion { get; init; } = 1;
    [Key(5)] public int SourceLayer { get; init; }      // 1 = базовый, 2 = комплексный
    [Key(6)] public int StreamId { get; init; } = -1;    // для слоя 2: к какому потоку относится
}
