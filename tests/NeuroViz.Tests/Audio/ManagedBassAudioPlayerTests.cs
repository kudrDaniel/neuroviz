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
}