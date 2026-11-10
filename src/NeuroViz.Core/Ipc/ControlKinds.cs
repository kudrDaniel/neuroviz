namespace NeuroViz.Core.Ipc;

public static class ControlKinds
{
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string LoadProject = "load_project";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Seek = "seek";
    public const string RulesUpdate = "rules_update";
    public const string Shutdown = "shutdown";

    // P2-B: window management, App → Godot. Sent after handshake, on
    // the long-lived Control pipe.
    public const string SetWindowMode = "set_window_mode";
    public const string SetAlwaysOnTop = "set_always_on_top";
    public const string SetVisualConfig = "set_visual_config";
    public const string SetVisualizerScene = "set_visualizer_scene";   // ← NEW
}

public static class IpcConstants
{
    public const int ProtocolVersion = 6;
    // Используем static get/set, чтобы App и Godot могли добавить PID процесса
    public static string ControlPipeName { get; set; } = "NeuroViz.Control";
    public static string EventStreamPipeName { get; set; } = "NeuroViz.EventStream";
    public static string AudioClockMapName { get; set; } = "NeuroViz.AudioClock";
    public static string PreviewPipeName { get; set; } = "NeuroViz.Preview";
}
