using NeuroViz.Core.Ipc;
using NeuroViz.Core.Serialization;

namespace NeuroViz.Tests;

public class IpcRoundTripTests
{
    [Fact]
    public void ControlMessage_RoundTrip()
    {
        var msg = new ControlMessage { Kind = ControlKinds.Seek, Payload = new byte[] { 1, 2, 3 } };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg, MessagePackOptions.Default);
        var back = MessagePack.MessagePackSerializer.Deserialize<ControlMessage>(bytes, MessagePackOptions.Default);
        Assert.Equal(ControlKinds.Seek, back.Kind);
        Assert.Equal(3, back.Payload.Length);
    }

    [Fact]
    public void EventStreamFrame_RoundTrip()
    {
        var frame = new EventStreamFrame
        {
            AudioClockSeconds = 3.14,
            FrameSeq = 42,
            Params = { ["pulse"] = new ParamValue { Value = 1f, SetAtSeconds = 3.0, DecayMs = 200 } }
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(frame, MessagePackOptions.Default);
        var back = MessagePack.MessagePackSerializer.Deserialize<EventStreamFrame>(bytes, MessagePackOptions.Default);
        Assert.Equal(3.14, back.AudioClockSeconds);
        Assert.Equal(1f, back.Params["pulse"].EvaluateAt(3.0), 3);
        Assert.True(back.Params["pulse"].EvaluateAt(3.5) < 0.5f);
    }
}