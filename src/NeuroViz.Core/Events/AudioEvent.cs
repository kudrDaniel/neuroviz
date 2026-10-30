using MessagePack;

namespace NeuroViz.Core.Events;

[MessagePackObject]
public sealed class AudioEvent
{
    [Key(0)] public string Type { get; init; } = "";
    [Key(1)] public double TimestampSeconds { get; init; }
    [Key(2)] public float Confidence { get; init; }
    [Key(3)] public Dictionary<string, float> Params { get; init; } = new();
    [Key(4)] public int SchemaVersion { get; init; } = 1;
    [Key(5)] public int SourceLayer { get; init; }      // 1 = базовый, 2 = комплексный
    [Key(6)] public int StreamId { get; init; } = -1;    // для слоя 2: к какому потоку относится
}