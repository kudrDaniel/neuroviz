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
    // Bumped 3→4 in P2-A: NeuroViz.Preview pipe (Godot→App) carries PNG
    // thumbnails. Non-breaking in practice: old App does not connect
    // (Godot-side server parks on WaitForConnectionAsync, no cost); old
    // Godot does not serve (App-side client retries once/sec, preview
    // stays null).
    public const int ProtocolVersion = 6;
    public const string ControlPipeName = "NeuroViz.Control";
    public const string EventStreamPipeName = "NeuroViz.EventStream";
    public const string AudioClockMapName = "NeuroViz.AudioClock";
    public const string PreviewPipeName = "NeuroViz.Preview";
}
