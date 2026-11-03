using NeuroViz.Analysis;

namespace NeuroViz.Tests.Analysis;

public class EventSinkTests
{
    [Fact]
    public void EmitContinuous_OverwritesValueForSameKey()
    {
        var sink = new EventSink();
        sink.EmitContinuous("a", 1f);
        sink.EmitContinuous("a", 2f);
        Assert.Equal(2f, sink.DrainContinuous()["a"]);
    }

    [Fact]
    public void DrainContinuous_Empty_WhenNothingEmitted()
    {
        var sink = new EventSink();
        Assert.Empty(sink.DrainContinuous());
    }

    [Fact]
    public void DrainContinuous_ClearsAccumulatedValues()
    {
        var sink = new EventSink();
        sink.EmitContinuous("a", 1f);
        sink.DrainContinuous();
        Assert.Empty(sink.DrainContinuous());
    }

    [Fact]
    public void DrainContinuous_ReturnsSnapshot_IndependentOfSubsequentWrites()
    {
        var sink = new EventSink();
        sink.EmitContinuous("a", 1f);
        var snap = sink.DrainContinuous();
        sink.EmitContinuous("a", 99f);
        Assert.Equal(1f, snap["a"]);
        Assert.Equal(99f, sink.DrainContinuous()["a"]);
    }
}
