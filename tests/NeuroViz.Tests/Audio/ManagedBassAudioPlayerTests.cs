using NeuroViz.Audio;

namespace NeuroViz.Tests.Audio;

public class ManagedBassAudioPlayerTests
{
    [Fact]
    public void Load_ReportsCorrectDurationAndFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nvtest-{Guid.NewGuid():N}.wav");
        ToneWavWriter.WriteSine(path, seconds: 2.0, sampleRate: 44100, channels: 2);
        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);

            Assert.Equal(2.0, player.Duration, 1);
            Assert.Equal(44100, player.SampleRate);
            Assert.Equal(2, player.Channels);
            Assert.Equal(0.0, player.Position, 1);
            Assert.False(player.IsPlaying);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NonExistentFile_Throws()
    {
        using var player = new ManagedBassAudioPlayer();
        Assert.Throws<FileNotFoundException>(() => player.Load("/no/such/file.wav"));
    }

    // Skip="..." не даёт тесту запуститься в CI и в обычном dotnet test.
    // Уберите Skip вручную и запустите:
    //   dotnet test --filter "FullyQualifiedName~Smoke_PlaysAndEmitsPcm"
    // Если слышите тон ~700 мс и счётчик кадров > 0 — ManagedBass работает.
    [Fact(Skip = "Manual smoke test: убрать Skip и запустить локально")]
    public async Task Smoke_PlaysAndEmitsPcm()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-smoke-{Guid.NewGuid():N}.wav");
        ToneWavWriter.WriteSine(path, seconds: 1.0, sampleRate: 44100, channels: 2);
        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);

            var frames = 0;
            player.PcmAvailable += (_, _) => Interlocked.Increment(ref frames);

            player.Play();
            await Task.Delay(700);
            player.Pause();

            Assert.True(frames > 0,
                $"PCM frames = 0. Bass.LastError = {ManagedBass.Bass.LastError}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractWaveformPeaks_UniformSine_ReturnsUniformEnvelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-wf-sine-{Guid.NewGuid():N}.wav");
        // 1 second, amplitude 16000 (of int16 max 32768) → ~0.488 as float.
        ToneWavWriter.WriteSine(path, seconds: 1.0, sampleRate: 44100, channels: 2);
        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);

            var peaks = player.ExtractWaveformPeaks(bucketCount: 100);

            Assert.Equal(100, peaks.Length);
            // All buckets ≈ 0.488 — sine amplitude uniform across the file.
            Assert.All(peaks, p => Assert.InRange(p, 0.45f, 0.52f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractWaveformPeaks_ClickTrain_PeaksAtClickTimes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-wf-clicks-{Guid.NewGuid():N}.wav");
        var clickTimes = new[] { 0.2, 0.5, 0.8 };
        ToneWavWriter.WriteClickTrain(path, totalSeconds: 1.0, clickTimes: clickTimes);
        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);

            var peaks = player.ExtractWaveformPeaks(bucketCount: 100);
            Assert.Equal(100, peaks.Length);

            for (int i = 0; i < peaks.Length; i++)
            {
                var tStart = i / 100.0;
                var tEnd = (i + 1) / 100.0;

                // Bucket fully inside [c, c+0.05] → must be loud.
                var fullyInside = clickTimes.Any(c => tStart >= c && tEnd <= c + 0.05);
                // Bucket has zero overlap with any click window → must be silent.
                var noOverlap = !clickTimes.Any(c => tEnd > c && tStart < c + 0.05);

                if (fullyInside)
                    Assert.True(peaks[i] > 0.3f,
                        $"bucket {i} [{tStart:F2}..{tEnd:F2}] inside click, got {peaks[i]:F3}");
                else if (noOverlap)
                    Assert.True(peaks[i] < 0.05f,
                        $"bucket {i} [{tStart:F2}..{tEnd:F2}] outside click, got {peaks[i]:F3}");
                // else: partial-overlap bucket (e.g. 0.20-0.21 covers click start
                // but Hann envelope is near zero there). No assertion — its peak
                // legitimately depends on envelope shape at the edge.
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractWaveformPeaks_RestoresPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-wf-pos-{Guid.NewGuid():N}.wav");
        ToneWavWriter.WriteSine(path, seconds: 2.0);
        try
        {
            BassBootstrap.EnsureInitialized();
            using var player = new ManagedBassAudioPlayer();
            player.Load(path);
            player.Seek(1.5);   // move to middle
            var posBefore = player.Position;

            player.ExtractWaveformPeaks(bucketCount: 50);

            Assert.Equal(posBefore, player.Position, 2); // within 10ms
        }
        finally
        {
            File.Delete(path);
        }
    }
}
