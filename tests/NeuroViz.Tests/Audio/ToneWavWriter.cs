namespace NeuroViz.Tests.Audio;

internal static class ToneWavWriter
{
    public static void WriteSine(string path, double seconds, int sampleRate = 44100, int channels = 2, double frequency = 440)
    {
        var samplesPerChannel = (int)(seconds * sampleRate);
        var totalSamples = samplesPerChannel * channels;
        var dataSize = totalSamples * 2; // 16-bit

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8.ToArray());

        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);                    // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);   // byte rate
        bw.Write((short)(channels * 2));       // block align
        bw.Write((short)16);                   // bits per sample

        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);

        for (int i = 0; i < samplesPerChannel; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 16000);
            for (int c = 0; c < channels; c++) bw.Write(s);
        }
    }

    public static void WriteClickTrain(
    string path,
    double totalSeconds,
    double[] clickTimes,
    double clickDurationSeconds = 0.05,
    int sampleRate = 44100,
    int channels = 2)
    {
        var samplesPerChannel = (int)(totalSeconds * sampleRate);
        var totalSamples = samplesPerChannel * channels;
        var dataSize = totalSamples * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);

        for (int i = 0; i < samplesPerChannel; i++)
        {
            var t = (double)i / sampleRate;
            double sample = 0;
            foreach (var ct in clickTimes)
            {
                if (t >= ct && t < ct + clickDurationSeconds)
                {
                    var phase = (t - ct) / clickDurationSeconds;
                    var env = Math.Sin(Math.PI * phase);  // окно Ханна по огибающей
                    sample += Math.Sin(2 * Math.PI * 1000 * t) * env * 0.7;
                }
            }
            var s = (short)(Math.Clamp(sample, -1.0, 1.0) * 32000);
            for (int c = 0; c < channels; c++) bw.Write(s);
        }
    }
}