using MessagePack;

namespace NeuroViz.Core.Events;

/// <summary>
/// One sample of all continuous metrics at a playback timestamp.
/// <see cref="Values"/> is positionally aligned with
/// <see cref="ContinuousKeys.Ordered"/>; array length is always
/// <see cref="ContinuousKeys.Count"/>.
///
/// Size on wire (LZ4-compressed): ~40 bytes/frame raw → ~10-20 bytes
/// compressed. A 4-minute track at 20 Hz ≈ 4800 frames ≈ ~50-100 KB.
/// </summary>
[MessagePackObject]
public sealed class ContinuousFrame
{
    [Key(0)] public double TimestampSeconds { get; init; }
    [Key(1)] public float[] Values { get; init; } = Array.Empty<float>();
}
