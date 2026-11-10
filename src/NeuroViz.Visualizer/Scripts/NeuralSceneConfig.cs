using Godot;

namespace NeuroViz.Visualizer.Scripts;

/// <summary>
/// Static holder for neural scene topology params. Filled by IpcClient
/// when SetVisualizerScene arrives (before ChangeSceneToFile); read by
/// MainNodeNeural._Ready before NeuralTopology.Generate().
///
/// Topology params travel with the scene switch (not SetVisualConfig)
/// because they take effect at _Ready of the new scene — live-tuning
/// would require a rebuild, which is exactly what the scene switch is.
/// </summary>
public static class NeuralSceneConfig
{
    public static int NodesPerHemisphere = 800;
    public static int EdgesPerNode = 4;
    public static float CCDensity = 0.3f;
    public static float CameraOrbitSpeed = 0.05f;
    public static float CameraYAmplitude = 0.4f;

    public static void InitializeFromCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        if (args == null) return;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--nv-ipc-suffix="))
            {
                var suffix = arg.Substring(16);
                NeuroViz.Core.Ipc.IpcConstants.ControlPipeName = $"NeuroViz.Control.{suffix}";
                NeuroViz.Core.Ipc.IpcConstants.EventStreamPipeName = $"NeuroViz.EventStream.{suffix}";
                NeuroViz.Core.Ipc.IpcConstants.PreviewPipeName = $"NeuroViz.Preview.{suffix}";
                NeuroViz.Core.Ipc.IpcConstants.AudioClockMapName = $"NeuroViz.AudioClock.{suffix}";
            }
            if (arg.StartsWith("--nv-nodes=") && int.TryParse(arg.Substring(11), out int nodes))
                NodesPerHemisphere = Math.Clamp(nodes, 200, 1500);
            else if (arg.StartsWith("--nv-edges=") && int.TryParse(arg.Substring(11), out int edges))
                EdgesPerNode = Math.Clamp(edges, 2, 6);
            else if (arg.StartsWith("--nv-cc=") && float.TryParse(arg.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cc))
                CCDensity = Math.Clamp(cc, 0.1f, 1.0f);
        }

        GD.Print($"[Neural] Config from cmdline: nodes={NodesPerHemisphere}, edges={EdgesPerNode}, cc={CCDensity}");
    }
}