using MessagePack;

namespace NeuroViz.Core.Ipc;

[MessagePackObject]
public sealed class ControlMessage
{
    [Key(0)] public string Kind { get; init; } = "";
    [Key(1)] public byte[] Payload { get; init; } = Array.Empty<byte>();
}

[MessagePackObject]
public sealed class HelloPayload
{
    [Key(0)] public int ProtocolVersion { get; init; }
    [Key(1)] public string Role { get; init; } = "";
}

[MessagePackObject]
public sealed class SeekPayload
{
    [Key(0)] public double Seconds { get; init; }
}

// P2-B: window management payloads.
[MessagePackObject]
public sealed class SetWindowModePayload
{
    [Key(0)] public int MonitorIndex { get; init; }
    [Key(1)] public bool Fullscreen { get; init; }
}

[MessagePackObject]
public sealed class SetAlwaysOnTopPayload
{
    [Key(0)] public bool Enabled { get; init; }
}
