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

    [Fact]
    public void EventStreamFrame_WithContinuousParams_RoundTrip()
    {
        var frame = new EventStreamFrame
        {
            AudioClockSeconds = 1.5,
            FrameSeq = 7,
            ContinuousParams =
        {
            ["spectral_centroid"] = 0.42f,
            ["harmonicity"] = 0.85f,
        },
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(frame, MessagePackOptions.Default);
        var back = MessagePack.MessagePackSerializer.Deserialize<EventStreamFrame>(bytes, MessagePackOptions.Default);
        Assert.Equal(0.42f, back.ContinuousParams["spectral_centroid"]);
        Assert.Equal(0.85f, back.ContinuousParams["harmonicity"]);
    }

    [Fact]
    public void EventStreamFrame_NoContinuousParams_DefaultsToEmpty()
    {
        // Simulates a v1 frame (from an old inspector) being read by v2 code:
        // ContinuousParams key is absent, must default to empty, not null.
        var legacy = new EventStreamFrame { AudioClockSeconds = 1.0, FrameSeq = 1 };
        var bytes = MessagePack.MessagePackSerializer.Serialize(legacy, MessagePackOptions.Default);
        var back = MessagePack.MessagePackSerializer.Deserialize<EventStreamFrame>(bytes, MessagePackOptions.Default);
        Assert.NotNull(back.ContinuousParams);
        Assert.Empty(back.ContinuousParams);
    }

    [Fact]
    public void PreviewFrame_RoundTrip()
    {
        var frame = new PreviewFrame
        {
            FrameSeq = 42,
            Width = 320,
            Height = 180,
            PngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 },   // PNG magic
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(frame, MessagePackOptions.Default);
        var back = MessagePack.MessagePackSerializer.Deserialize<PreviewFrame>(bytes, MessagePackOptions.Default);

        Assert.Equal(42, back.FrameSeq);
        Assert.Equal(320, back.Width);
        Assert.Equal(180, back.Height);
        Assert.Equal(4, back.PngBytes.Length);
        Assert.Equal(0x89, back.PngBytes[0]);
    }
}
