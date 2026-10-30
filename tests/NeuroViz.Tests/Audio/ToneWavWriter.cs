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
}