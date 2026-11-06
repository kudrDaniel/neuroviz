using NeuroViz.Core.Events;

namespace NeuroViz.Tests;

public class ContinuousInterpolationTests
{
    [Fact]
    public void EmptyList_ReturnsEmptyDict()
    {
        var result = ContinuousInterpolation.EvaluateAt(
            Array.Empty<ContinuousFrame>(), 5.0);
        Assert.Empty(result);
    }

    [Fact]
    public void BeforeFirstFrame_ClampsToFirst()
    {
        var frames = new[] { MakeFrame(2.0, bandLow: 0.3f) };
        var r = ContinuousInterpolation.EvaluateAt(frames, 0.5);
        Assert.Equal(0.3f, r[ContinuousKeys.BandEnergyLow]);
    }

    [Fact]
    public void AfterLastFrame_ClampsToLast()
    {
        var frames = new[] { MakeFrame(2.0, bandLow: 0.7f) };
        var r = ContinuousInterpolation.EvaluateAt(frames, 100.0);
        Assert.Equal(0.7f, r[ContinuousKeys.BandEnergyLow]);
    }

    [Fact]
    public void BetweenTwoFrames_LinearInterpolation()
    {
        var frames = new[]
        {
            MakeFrame(1.0, bandLow: 0.0f),
            MakeFrame(3.0, bandLow: 1.0f),
        };
        // pos 2.0 is halfway → 0.5
        var r = ContinuousInterpolation.EvaluateAt(frames, 2.0);
        Assert.Equal(0.5f, r[ContinuousKeys.BandEnergyLow], 4);

        // pos 1.5 → 0.25
        var r15 = ContinuousInterpolation.EvaluateAt(frames, 1.5);
        Assert.Equal(0.25f, r15[ContinuousKeys.BandEnergyLow], 4);

        // pos 2.5 → 0.75
        var r25 = ContinuousInterpolation.EvaluateAt(frames, 2.5);
        Assert.Equal(0.75f, r25[ContinuousKeys.BandEnergyLow], 4);
    }

    [Fact]
    public void ManyFrames_PicksCorrectSegment()
    {
        var frames = new[]
        {
            MakeFrame(0.0, bandLow: 0.0f),
            MakeFrame(1.0, bandLow: 1.0f),
            MakeFrame(2.0, bandLow: 0.0f),
            MakeFrame(3.0, bandLow: 1.0f),
        };
        // pos 2.5 → between (2.0, 0) and (3.0, 1) → 0.5
        var r = ContinuousInterpolation.EvaluateAt(frames, 2.5);
        Assert.Equal(0.5f, r[ContinuousKeys.BandEnergyLow], 4);
    }

    [Fact]
    public void ExactlyOnFrameTimestamp_ReturnsExact()
    {
        var frames = new[]
        {
            MakeFrame(1.0, bandLow: 0.2f),
            MakeFrame(2.0, bandLow: 0.8f),
        };
        var r = ContinuousInterpolation.EvaluateAt(frames, 2.0);
        Assert.Equal(0.8f, r[ContinuousKeys.BandEnergyLow], 4);
    }

    [Fact]
    public void ReturnsAllEightKeys()
    {
        var frames = new[] { MakeFrame(1.0, bandLow: 0.5f) };
        var r = ContinuousInterpolation.EvaluateAt(frames, 1.0);
        Assert.Equal(ContinuousKeys.Count, r.Count);
        foreach (var key in ContinuousKeys.Ordered)
            Assert.True(r.ContainsKey(key), $"missing key {key}");
    }

    private static ContinuousFrame MakeFrame(double t, float bandLow)
    {
        var values = new float[ContinuousKeys.Count];
        values[Array.IndexOf(ContinuousKeys.Ordered, ContinuousKeys.BandEnergyLow)] = bandLow;
        return new ContinuousFrame { TimestampSeconds = t, Values = values };
    }
}
