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

// 2.4f: visualizer tuning payload.
[MessagePackObject]
public sealed class SetVisualConfigPayload
{
    [Key(0)] public float CameraOrbitSpeed { get; init; }
    [Key(1)] public float CameraYAmplitude { get; init; }
    [Key(2)] public int AmbientParticleCount { get; init; }
    [Key(3)] public int BurstParticleCount { get; init; }
    [Key(4)] public float NeuralCameraOrbitSpeed { get; init; }
    [Key(5)] public float NeuralCameraYAmplitude { get; init; }
}

// N.9: scene switch + topology params. Sent when the user changes the
// scene ComboBox or on Launch. Topology params travel with the scene
// switch (not SetVisualConfig) because they take effect at _Ready of
// the new scene, not live.
[MessagePackObject]
public sealed class SetVisualizerScenePayload
{
    [Key(0)] public string Scene { get; init; } = "spike";
    [Key(1)] public int NeuralNodesPerHemisphere { get; init; }
    [Key(2)] public int NeuralEdgesPerNode { get; init; }
    [Key(3)] public float NeuralCCDensity { get; init; }
}
