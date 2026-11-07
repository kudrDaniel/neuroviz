using Godot;

namespace NeuroViz.Visualizer.Scripts;

/// <summary>
/// N.1 stub: generates the topology and logs diagnostics. No rendering
/// yet. N.2 will add MultiMeshInstance3D for nodes and ArrayMesh for
/// edges. IPC is wired here so it's ready from N.3 (cascade) onwards.
/// </summary>
public partial class MainNodeNeural : Node3D
{
    private IpcClient _ipc = null!;
    private NeuralTopology _topology = null!;
    private Label _fpsLabel = null!;

    public override void _Ready()
    {
        _topology = NeuralTopology.Generate(
            nodesPerHemisphere: 800,
            edgesPerNode: 4,
            ccDensity: 0.3f,
            seed: 42);

        int cc = _topology.Edges.Count(e => e.IsCrossCallosum);
        GD.Print($"[Neural] Generated: {_topology.Nodes.Count} nodes, " +
                 $"{_topology.Edges.Count} edges ({cc} CC).");
        LogLayerCentroids();

        // Minimal scene so the spike-style preview capture works even in N.1.
        // Nothing visible yet (no meshes), but the camera/light set the stage.
        var camera = new Camera3D { Position = new Vector3(0.5f, 0.6f, 2.4f) };
        AddChild(camera);
        camera.LookAt(new Vector3(0.5f, 0, 0));

        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(-0.5f, 0.7f, 0),
            LightEnergy = 1.2f,
            ShadowEnabled = true,
        });

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.03f, 0.03f, 0.06f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.1f, 0.1f, 0.15f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        _fpsLabel = new Label { Position = new Vector2(10, 10) };
        AddChild(_fpsLabel);

        _ipc = new IpcClient();
        _ipc.Start();
    }

    private void LogLayerCentroids()
    {
        for (int hemi = 0; hemi < 2; hemi++)
        {
            for (int layer = 0; layer < 3; layer++)
            {
                var layerNodes = _topology.Nodes
                    .Where(n => n.Hemisphere == hemi && n.Layer == layer)
                    .ToList();
                if (layerNodes.Count == 0) continue;
                float cx = layerNodes.Average(n => n.Position.X);
                float cy = layerNodes.Average(n => n.Position.Y);
                float cz = layerNodes.Average(n => n.Position.Z);
                GD.Print($"[Neural] Hemi {hemi}, Layer {layer}: {layerNodes.Count} nodes, " +
                         $"centroid ({cx:F3}, {cy:F3}, {cz:F3})");
            }
        }
    }

    public override void _Process(double delta)
    {
        _ipc.DrainMainThreadActions();
        _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond():F0} | " +
                         $"Nodes: {_topology.Nodes.Count} | " +
                         $"Edges: {_topology.Edges.Count} (stub N.1)";
    }

    public override void _ExitTree() => _ipc.Dispose();
}