using NeuroViz.Core.Events;
using NeuroViz.Project;
using NeuroViz.Core.Rules;

namespace NeuroViz.Tests;

public class ProjectRoundTripTests
{
    [Fact]
    public void RoundTrip_PreservesEvents()
    {
        var timeline = new EventTimeline
        {
            AudioPath = "/tmp/a.wav",
            DurationSeconds = 12.5,
            Events =
            {
                new AudioEvent
                {
                    Type = EventTypes.Onset,
                    TimestampSeconds = 1.23,
                    Confidence = 0.91f,
                    SourceLayer = 1,
                    Params = { ["band"] = 0.0f, ["rms"] = 0.42f }
                }
            }
        };
        var project = new ProjectDocument { Name = "t", Timeline = timeline };
        var bytes = ProjectSerializer.Serialize(project);
        var back = ProjectSerializer.Deserialize(bytes);
        Assert.Single(back.Timeline.Events);
        Assert.Equal("onset", back.Timeline.Events[0].Type);
        Assert.Equal(0.42f, back.Timeline.Events[0].Params["rms"]);
    }

    [Fact]
    public void RoundTrip_PreservesRules()
    {
        var project = new ProjectDocument
        {
            Name = "t",
            Rules = new RulesDocument
            {
                Version = 1,
                Rules =
            {
                new Rule
                {
                    Id = "onset-pulse",
                    Priority = 100,
                    When = new RuleCondition { Event = EventTypes.Onset },
                    Actions =
                    {
                        new RuleAction { Param = "pulse", Value = 1f, DecayMs = 150 }
                    }
                }
            }
            }
        };

        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.Single(back.Rules.Rules);
        Assert.Equal("onset-pulse", back.Rules.Rules[0].Id);
        Assert.Equal(EventTypes.Onset, back.Rules.Rules[0].When.Event);
        Assert.Single(back.Rules.Rules[0].Actions);
        Assert.Equal("pulse", back.Rules.Rules[0].Actions[0].Param);
        Assert.Equal(150, back.Rules.Rules[0].Actions[0].DecayMs);
    }
}