using NeuroViz.Audio;

namespace NeuroViz.Tests.Audio;

public class ManagedBassAudioPlayerSmokeTests
{
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