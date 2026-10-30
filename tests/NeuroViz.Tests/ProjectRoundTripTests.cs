using NeuroViz.Core.Events;
using NeuroViz.Project;

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
}