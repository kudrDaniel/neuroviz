using Godot;
using System;

namespace NeuroViz.Visualizer.Scripts;

public partial class IpcBootstrapHolder : Node
{
    private static IpcBootstrapHolder? _instance;
    private IpcClient _client = null!;

    public static IpcClient Client =>
        _instance?._client ?? throw new InvalidOperationException(
            "IpcBootstrapHolder not initialized. Is bootstrap.tscn the main scene?");

    public override void _Ready()
    {
        _instance = this;
        _client = new IpcClient();
        _client.Start();
        GD.Print("[Bootstrap] IpcClient started (process-lifetime).");
    }

    public override void _Process(double delta)
    {
        // Единственное место, где обрабатывается смена сцены
        if (_client.PendingScenePath is { } path)
        {
            _client.ClearPendingScene();
            var current = GetTree().CurrentScene?.SceneFilePath;
            if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
            {
                GD.Print($"[Bootstrap] Scene switch: {current} -> {path}");
                GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
            }
        }
    }

    public override void _ExitTree()
    {
        GD.Print("[Bootstrap] Shutting down IpcClient.");
        _client?.Dispose();
        _instance = null;
    }
}