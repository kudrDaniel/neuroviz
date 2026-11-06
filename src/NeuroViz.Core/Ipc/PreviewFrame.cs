using MessagePack;

namespace NeuroViz.Core.Ipc;

/// <summary>
/// One preview-thumbnail frame: PNG bytes captured by the Visualizer and
/// streamed back to the App over the NeuroViz.Preview pipe. Direction is
/// Godot → App, reversed relative to Control and EventStream.
///
/// PNG (not raw RGBA) keeps the wire at 5–20 KB/frame for a 320×180 dark
/// scene. At 5 fps that is ~25–100 KB/s — negligible.
/// </summary>
[MessagePackObject]
public sealed class PreviewFrame
{
    [Key(0)] public long FrameSeq { get; init; }
    [Key(1)] public int Width { get; init; }
    [Key(2)] public int Height { get; init; }
    [Key(3)] public byte[] PngBytes { get; init; } = Array.Empty<byte>();
}
