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
}