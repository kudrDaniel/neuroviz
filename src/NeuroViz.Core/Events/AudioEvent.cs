using MessagePack;

namespace NeuroViz.Core.Events;

[MessagePackObject]
public sealed class AudioEvent
{
    [Key(0)] public string Type { get; init; } = "";
    [Key(1)] public double TimestampSeconds { get; init; }
    [Key(2)] public float Confidence { get; init; }
    [Key(3)] public Dictionary<string, float> Params { get; init; } = new();
    [Key(4)] public int SchemaVersion { get; init; } = 2;   // was 1 in v1 projects
    [Key(5)] public int SourceLayer { get; init; }
    [Key(6)] public int StreamId { get; init; } = -1;

    /// <summary>
    /// 32-band downsampled magnitude snapshot at the event's timestamp.
    /// Populated when the event is created from a live SpectralFrame
    /// (<see cref="NeuroViz.App.ViewModels.EventRow.From"/>) or restored
    /// from a v2 .nvproj. Empty array for v1 projects — see
    /// EventRow.FromStored fallback to new float[32].
    ///
    /// <para><b>Wire-compatible:</b> old readers ignore Key(7); new
    /// readers see missing key in v1 payloads and fall back to the
    /// Array.Empty&lt;float&gt; initializer. No IpcConstants.ProtocolVersion
    /// bump needed.</para>
    /// </summary>
    [Key(7)] public float[] DownsampledSpectrum { get; init; } = Array.Empty<float>();
}
