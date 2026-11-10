using Godot;

namespace NeuroViz.Visualizer.Scripts;

public partial class Bootstrap : Node
{
    public override void _Ready()
    {
        NeuralSceneConfig.InitializeFromCommandLine();

        var holder = new IpcBootstrapHolder();
        // Откладываем добавление в корень на конец кадра
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, holder);

        var path = NeuralSceneConfig.InitialScene switch
        {
            "2d" => "res://node_2d.tscn",
            "spike" => "res://spike.tscn",
            _ => "res://neural.tscn",
        };

        GD.Print($"[Bootstrap] Starting with scene: {path}");
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
    }
}