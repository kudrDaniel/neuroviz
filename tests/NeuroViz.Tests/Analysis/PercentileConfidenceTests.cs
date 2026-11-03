using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class PercentileConfidenceTests
{
    [Fact]
    public void WarmUp_ReturnsFallback()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);

        Assert.False(model.IsWarmedUp);
        Assert.Equal(0.5f, model.Estimate(0.9f));
        model.Observe(0.4f);
        Assert.Equal(0.5f, model.Estimate(0.9f));
        model.Observe(0.5f);
        Assert.Equal(0.5f, model.Estimate(0.9f));

        // Third observation → warmed up.
        model.Observe(0.6f);
        Assert.True(model.IsWarmedUp);
        // 0.9 > all three → rank = 1.0.
        Assert.Equal(1.0f, model.Estimate(0.9f));
    }

    [Fact]
    public void HighestUnique_ReturnsOne()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);
        model.Observe(0.1f);
        model.Observe(0.2f);
        model.Observe(0.3f);
        Assert.Equal(1.0f, model.Estimate(0.5f));
    }

    [Fact]
    public void LowestUnique_ReturnsZero()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);
        model.Observe(0.2f);
        model.Observe(0.3f);
        model.Observe(0.4f);
        Assert.Equal(0f, model.Estimate(0.1f));
    }

    [Fact]
    public void BetweenValues_MidrankMatchesFormula()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);
        model.Observe(0.1f);
        model.Observe(0.2f);
        model.Observe(0.3f);
        // Production contract: Estimate runs BEFORE Observe for the same
        // event, so the candidate is not in history. 0.15 is strictly above
        // only 0.1 → mid-rank = 1/3.
        Assert.Equal(1f / 3f, model.Estimate(0.15f), 4);
    }

    [Fact]
    public void ValueInHistory_TieMidrankHalf()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);
        model.Observe(0.1f);
        model.Observe(0.2f);
        model.Observe(0.3f);
        // Defensive case (candidate equals a value already in history):
        // 0.2 is the median — 1 strictly below, 1 tied → (1 + 0.5) / 3 = 0.5.
        // Not reachable from OnsetDetector (Estimate-then-Observe), but the
        // model must still behave consistently for any caller.
        Assert.Equal(0.5f, model.Estimate(0.2f), 4);
    }

    [Fact]
    public void AllEqual_ReturnsHalf()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 3);
        model.Observe(0.5f);
        model.Observe(0.5f);
        model.Observe(0.5f);
        // All ties → mid-rank = 0.5.
        Assert.Equal(0.5f, model.Estimate(0.5f));
    }

    [Fact]
    public void SlidingWindow_EvictsOldest()
    {
        var model = new PercentileConfidence(capacity: 3, minHistory: 1);
        model.Observe(0.1f);
        model.Observe(0.2f);
        model.Observe(0.3f);
        model.Observe(0.4f); // evicts 0.1
        model.Observe(0.5f); // evicts 0.2

        // History = [0.3, 0.4, 0.5]. 0.35 is above 0.3 only → 1/3.
        Assert.Equal(1f / 3f, model.Estimate(0.35f), 4);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var model = new PercentileConfidence(capacity: 10, minHistory: 2);
        model.Observe(0.5f);
        model.Observe(0.6f);
        Assert.True(model.IsWarmedUp);

        model.Reset();
        Assert.False(model.IsWarmedUp);
        Assert.Equal(0.5f, model.Estimate(0.7f));
    }

    [Fact]
    public void Ctor_RejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PercentileConfidence(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PercentileConfidence(capacity: 5, minHistory: 6));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PercentileConfidence(warmupFallback: 1.5f));
    }
}
