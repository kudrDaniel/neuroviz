using NeuroViz.Core.Events;
using NeuroViz.Core.Rules;

namespace NeuroViz.Tests;

public class RuleEngineTests
{
    private static RuleEngine MakeEngine()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic.json");
        return new RuleEngine(RulesLoader.FromFile(path));
    }

    [Fact]
    public void OnsetAboveThreshold_TriggersPulse()
    {
        var engine = MakeEngine();
        var ev = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.9f, TimestampSeconds = 1.0 };
        var actions = engine.Process(ev, 1.0).ToList();
        Assert.Contains(actions, a => a.Action.Param == "pulse");
    }

    [Fact]
    public void OnsetBelowThreshold_NoPulse()
    {
        var engine = MakeEngine();
        var ev = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.5f, TimestampSeconds = 1.0 };
        var actions = engine.Process(ev, 1.0).ToList();
        Assert.DoesNotContain(actions, a => a.Action.Param == "pulse");
    }
}