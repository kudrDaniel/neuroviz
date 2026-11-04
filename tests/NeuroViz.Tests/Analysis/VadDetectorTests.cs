using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class VadDetectorTests
{
    [Fact]
    public void Silence_VadOff_RatioDecays()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // Silence frame — no energy.
        det.ProcessContinuous(MakeFrame(new float[1025], zcr: 0f), sink);
        var d = sink.DrainContinuous();

        Assert.Equal(0f, d["vad_state"]);
        Assert.Equal(0f, d["speech_music_ratio"]);
    }

    [Fact]
    public void StrongVoice_VadOn()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // Voice-like: white noise floor 0.1 + formant peaks 20–80 at mag 15
        // → centroid ≈ 2 kHz, flatness ≈ 0.14, voiceFraction ≈ 1.0.
        var frame = VoiceFrame();
        det.ProcessContinuous(frame, sink);
        var d = sink.DrainContinuous();

        Assert.Equal(1f, d["vad_state"]);
    }

    [Fact]
    public void BassPureTone_VadOff()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // Bass 107 Hz pure tone: centroid 107, flatness ≈ 0, ZCR 0.005.
        var mags = new float[1025];
        mags[5] = 100f;
        det.ProcessContinuous(MakeFrame(mags, zcr: 0.005f), sink);
        var d = sink.DrainContinuous();

        Assert.Equal(0f, d["vad_state"]);
    }

    [Fact]
    public void WhiteNoise_VadOff()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // White noise: flatness ≈ 1 (out of speech range), ZCR 0.5.
        var mags = new float[1025];
        var rng = new Random(42);
        for (int i = 1; i < mags.Length; i++)
            mags[i] = 0.5f + (float)rng.NextDouble();
        det.ProcessContinuous(MakeFrame(mags, zcr: 0.5f), sink);
        var d = sink.DrainContinuous();

        Assert.Equal(0f, d["vad_state"]);
    }

    [Fact]
    public void Hysteresis_StaysOnUntilBelowExitThreshold()
    {
        var sink = new EventSink();
        var det = new VadDetector(vadOnThreshold: 0.7f, vadOffThreshold: 0.5f);

        det.ProcessContinuous(VoiceFrame(), sink);
        Assert.True(1f == sink.DrainContinuous()["vad_state"], "after strong voice");

        det.ProcessContinuous(VoiceFrame(zcr: 0.3f), sink);
        Assert.True(1f == sink.DrainContinuous()["vad_state"], "borderline should hold ON");

        var mags = new float[1025];
        mags[5] = 100f;
        det.ProcessContinuous(MakeFrame(mags, zcr: 0.005f), sink);
        Assert.True(0f == sink.DrainContinuous()["vad_state"], "bass should turn OFF");
    }

    [Fact]
    public void SpeechMusicRatio_RisesOnSustainedVoice()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // Feed 200 voice frames; EMA with α ≈ 0.012 should climb to ~1.
        for (int i = 0; i < 200; i++)
            det.ProcessContinuous(VoiceFrame(), sink);

        var ratio = sink.DrainContinuous()["speech_music_ratio"];
        Assert.True(ratio > 0.7f,
            $"after 200 voice frames ratio={ratio:F3}, expected >0.7");
    }

    [Fact]
    public void SpeechMusicRatio_FallsOnSilence()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        // Prime ratio high with voice.
        for (int i = 0; i < 200; i++)
            det.ProcessContinuous(VoiceFrame(), sink);
        sink.DrainContinuous();

        // 200 silence frames.
        for (int i = 0; i < 200; i++)
            det.ProcessContinuous(MakeFrame(new float[1025], zcr: 0f), sink);

        var ratio = sink.DrainContinuous()["speech_music_ratio"];
        Assert.True(ratio < 0.3f,
            $"after 200 silence frames ratio={ratio:F3}, expected <0.3");
    }

    [Fact]
    public void Reset_ClearsVadAndRatio()
    {
        var sink = new EventSink();
        var det = new VadDetector();

        for (int i = 0; i < 100; i++)
            det.ProcessContinuous(VoiceFrame(), sink);
        sink.DrainContinuous();

        det.Reset();

        det.ProcessContinuous(MakeFrame(new float[1025], zcr: 0f), sink);
        var d = sink.DrainContinuous();
        Assert.Equal(0f, d["vad_state"]);
        Assert.Equal(0f, d["speech_music_ratio"]);
    }

    // ── helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Voice-like synthetic spectrum: white noise floor 0.1 + 61 formant
    /// peaks (bins 20–80) at magnitude 15. Empirically: centroid ≈ 2 kHz,
    /// flatness ≈ 0.14, voiceFraction ≈ 1.0. With ZCR 0.1 this is a
    /// confident "voice" frame.
    /// </summary>
    private static SpectralFrame VoiceFrame(float zcr = 0.1f)
    {
        var mags = new float[1025];
        for (int i = 1; i < mags.Length; i++) mags[i] = 0.1f;
        for (int i = 20; i <= 80; i++) mags[i] = 15f;
        return MakeFrame(mags, zcr);
    }

    private static SpectralFrame MakeFrame(float[] mags, float zcr) => new()
    {
        Magnitudes = mags,
        TimestampSeconds = 0,
        SampleRate = 44100,
        FftSize = 2048,
        Sequence = 0,
        ZeroCrossingRate = zcr,
    };
}
