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
}