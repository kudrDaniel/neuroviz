using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Audio;
using NeuroViz.Core.Events;
using NeuroViz.Tests.Audio;

namespace NeuroViz.Tests.Analysis;

public class AnalyzerPipelineIntegrationTests
{
    [Fact]
    public async Task DetectsClicksInClickTrain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-clicks-{Guid.NewGuid():N}.wav");
        var clickTimes = new[] { 0.2, 0.5, 0.8 };
        ToneWavWriter.WriteClickTrain(path, totalSeconds: 1.0, clickTimes: clickTimes);

        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);

            using var pipeline = new AnalyzerPipeline(
                player.SampleRate, player.Channels, fftSize: 1024, hopSize: 256);
            pipeline.AddDetector(new OnsetDetector(historySize: 15, thresholdMult: 1.2f));

            var events = new List<AudioEvent>();
            pipeline.Sink.EventEmitted += ev =>
            {
                lock (events) events.Add(ev.Event);
            };

            pipeline.Attach(player);
            player.Play();
            await Task.Delay(1500);
            player.Pause();
            pipeline.Detach();

            List<AudioEvent> snapshot;
            lock (events) snapshot = events.ToList();

            var onsetTimes = snapshot
                .Where(e => e.Type == EventTypes.Onset)
                .Select(e => e.TimestampSeconds)
                .OrderBy(t => t)
                .ToList();

            Assert.True(onsetTimes.Count >= 2,
                $"Expected ≥2 onsets, got {onsetTimes.Count}. Times: [{string.Join(", ", onsetTimes)}]");

            foreach (var expected in clickTimes)
            {
                var match = onsetTimes.Any(t => Math.Abs(t - expected) < 0.15);
                Assert.True(match,
                    $"No onset near {expected}s. Detected: [{string.Join(", ", onsetTimes)}]");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Skip = "Manual: наблюдать события вживую")]
    public async Task Smoke_PrintEvents()
    {
        BassBootstrap.EnsureInitialized();
        using var player = new ManagedBassAudioPlayer();
        player.Load("F:/Administrator/Music/aangelcry/album-3-18-12-21/1-8-03-41.mp3");
        using var pipeline = new AnalyzerPipeline(player.SampleRate, player.Channels);
        pipeline.AddDetector(new OnsetDetector());
        pipeline.Sink.EventEmitted += ev =>
            Console.WriteLine($"[{ev.Event.TimestampSeconds:F3}] {ev.Event.Type} conf={ev.Event.Confidence:F2}");
        pipeline.Attach(player);
        player.Play();
        await Task.Delay(TimeSpan.FromSeconds(20));
        player.Pause();
    }
}