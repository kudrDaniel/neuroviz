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
}

public static class IpcConstants
{
    public const int ProtocolVersion = 1;
    public const string ControlPipeName = "NeuroViz.Control";
    public const string EventStreamPipeName = "NeuroViz.EventStream";
    public const string AudioClockMapName = "NeuroViz.AudioClock";
}