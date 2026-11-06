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
}

public static class IpcConstants
{
    // Bumped 1→2 in B2: added EventStreamFrame.ContinuousParams ([Key(4)]).
    // Wire-compatible at MessagePack level (unknown keys are ignored both
    // directions), but semantically the protocol gained a new channel.
    public const int ProtocolVersion = 3;
    public const string ControlPipeName = "NeuroViz.Control";
    public const string EventStreamPipeName = "NeuroViz.EventStream";
    public const string AudioClockMapName = "NeuroViz.AudioClock";
}
