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
        Assert.Contains(actions, a => a.Param == "pulse");
    }

    [Fact]
    public void OnsetBelowThreshold_NoPulse()
    {
        var engine = MakeEngine();
        var ev = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.5f, TimestampSeconds = 1.0 };
        var actions = engine.Process(ev, 1.0).ToList();
        Assert.DoesNotContain(actions, a => a.Param == "pulse");
    }

    [Fact]
    public void Process_YieldsActionsInDescendingPriorityOrder()
    {
        var doc = RulesLoader.FromJson("""
    {
      "version": 1,
      "rules": [
        { "id": "low",  "priority": 10, "when": { "event": "onset" },
          "actions": [ { "type": "set", "param": "pulse", "value": 0.3, "decayMs": 100 } ] },
        { "id": "high", "priority": 99, "when": { "event": "onset" },
          "actions": [ { "type": "set", "param": "pulse", "value": 1.0, "decayMs": 100 } ] }
      ]
    }
    """);

        var engine = new RuleEngine(doc);
        var ev = new AudioEvent { Type = EventTypes.Onset };
        var actions = engine.Process(ev, 0.0).ToList();

        Assert.Equal(2, actions.Count);
        Assert.Equal("high", actions[0].RuleId);
        Assert.Equal("low", actions[1].RuleId);
    }

    [Fact]
    public void StreamBoundary_Open_TriggersStreamGlow()
    {
        var engine = MakeEngine();
        var ev = new AudioEvent
        {
            Type = EventTypes.StreamBoundary,
            TimestampSeconds = 10,
            Params = new Dictionary<string, float> { ["open"] = 1f },
        };
        var actions = engine.Process(ev, 10).ToList();

        Assert.Contains(actions, a => a.Param == "stream_glow");
        Assert.DoesNotContain(actions, a => a.Param == "stream_fade");
    }

    [Fact]
    public void StreamBoundary_Close_TriggersStreamFade()
    {
        var engine = MakeEngine();
        var ev = new AudioEvent
        {
            Type = EventTypes.StreamBoundary,
            TimestampSeconds = 20,
            Params = new Dictionary<string, float> { ["open"] = 0f },
        };
        var actions = engine.Process(ev, 20).ToList();

        Assert.Contains(actions, a => a.Param == "stream_fade");
        Assert.DoesNotContain(actions, a => a.Param == "stream_glow");
    }
}
