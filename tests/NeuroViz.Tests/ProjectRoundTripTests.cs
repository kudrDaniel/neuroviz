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

    [Fact]
    public void DownsampledSpectrum_RoundTrip()
    {
        var spectrum = Enumerable.Range(0, 32).Select(i => i / 32f).ToArray();
        var project = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline
            {
                Events =
            {
                new AudioEvent
                {
                    Type = EventTypes.Onset,
                    TimestampSeconds = 1.23,
                    Confidence = 0.9f,
                    DownsampledSpectrum = spectrum,
                },
            },
            },
        };

        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var restored = back.Timeline.Events[0].DownsampledSpectrum;
        Assert.Equal(32, restored.Length);
        for (int i = 0; i < 32; i++)
            Assert.Equal(i / 32f, restored[i], 5);
    }

    [Fact]
    public void DownsampledSpectrum_MissingInPayload_DefaultsToEmpty()
    {
        // Simulates a v1 project: AudioEvent without Key(7). Property
        // initializer runs before MessagePack fills keys; missing key
        // leaves the initializer value (Array.Empty<float>).
        var ev = new AudioEvent { Type = EventTypes.Onset, TimestampSeconds = 1.0 };
        Assert.Empty(ev.DownsampledSpectrum);

        var bytes = ProjectSerializer.Serialize(new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline { Events = { ev } },
        });
        var back = ProjectSerializer.Deserialize(bytes);

        Assert.NotNull(back.Timeline.Events[0].DownsampledSpectrum);
        Assert.Empty(back.Timeline.Events[0].DownsampledSpectrum);
    }

    [Fact]
    public void ContinuousFrames_RoundTrip()
    {
        var doc = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline
            {
                ContinuousFrames =
            {
                new ContinuousFrame
                {
                    TimestampSeconds = 0.0,
                    Values = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0f, 0f, 0.6f },
                },
                new ContinuousFrame
                {
                    TimestampSeconds = 1.5,
                    Values = new float[] { 0.7f, 0.8f, 0.9f, 1.0f, 0.5f, 1f, 0.5f, 0.3f },
                },
            },
            },
        };

        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(doc));

        Assert.Equal(2, back.Timeline.ContinuousFrames.Count);
        Assert.Equal(0.0, back.Timeline.ContinuousFrames[0].TimestampSeconds);
        Assert.Equal(0.1f, back.Timeline.ContinuousFrames[0].Values[0]);
        Assert.Equal(1.5, back.Timeline.ContinuousFrames[1].TimestampSeconds);
        Assert.Equal(0.9f, back.Timeline.ContinuousFrames[1].Values[2]);
    }

    [Fact]
    public void ContinuousFrames_MissingInPayload_DefaultsToEmpty()
    {
        // v2 project: EventTimeline without Key(6). Property initializer
        // provides empty list; MessagePack leaves it untouched.
        var timeline = new EventTimeline();
        Assert.Empty(timeline.ContinuousFrames);

        var doc = new ProjectDocument { Name = "t", Timeline = timeline };
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(doc));

        Assert.NotNull(back.Timeline.ContinuousFrames);
        Assert.Empty(back.Timeline.ContinuousFrames);
    }

    [Fact]
    public void SchemaVersions_AreThree()
    {
        Assert.Equal(3, ProjectDocument.CurrentVersion);
        Assert.Equal(3, new EventTimeline().SchemaVersion);
        Assert.Equal(2, new AudioEvent().SchemaVersion);   // unchanged in v3
    }
}
