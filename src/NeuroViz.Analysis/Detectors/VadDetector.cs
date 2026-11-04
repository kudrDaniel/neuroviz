using System;

namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Voice Activity Detection + speech/music ratio, emitted as continuous
/// values each frame:
/// <list type="bullet">
///   <item><c>vad_state</c> — 0 or 1, with hysteresis.</item>
///   <item><c>speech_music_ratio</c> — 0..1, EMA of vad_state, τ ≈ 2 s.</item>
/// </list>
///
/// <para><b>Score model:</b> soft-min of four features. Each feature is
/// in [0, 1], with a soft window in its characteristic range. The final
/// score is <c>0.7·min + 0.3·avg</c> — this punishes any single failure
/// (a bass line has flatness ≈ 0 → score 0 despite centroid ≈ 107) while
/// not collapsing to zero when one feature is only slightly marginal.
/// See TUNING.md for per-feature windows.</para>
///
/// <para><b>Features:</b> voice-band fraction (300–3000 Hz), spectral
/// centroid (speech ≈ 1–2 kHz), spectral flatness (speech is not pure
/// tone, not white noise), zero-crossing rate (speech 0.02–0.20). ZCR
/// comes from <see cref="SpectralFrame.ZeroCrossingRate"/> — filled by
/// StftBuffer from raw time-domain samples.</para>
/// </summary>
public sealed class VadDetector : IContinuousDetector
{
    // Voice band (fundamental + first formants).
    private const float VoiceLoHz = 300f;
    private const float VoiceHiHz = 3000f;

    // Centroid window (speech typically 1–2 kHz for mixed voice).
    private const float CentroidLoHz = 300f;
    private const float CentroidHiHz = 2500f;

    // Flatness window: pure tone = 0, white noise = 1, speech = 0.1–0.5.
    private const float FlatnessLo = 0.05f;
    private const float FlatnessHi = 0.55f;

    // ZCR window: bass/tone ≈ 0.005, voice 0.02–0.20, noise/fricatives > 0.3.
    private const float ZcrLo = 0.02f;
    private const float ZcrHi = 0.20f;

    private readonly float _ratioAlpha;
    private readonly float _vadOn;
    private readonly float _vadOff;

    private bool _isVadOn;
    private float _speechMusicRatio;

    public VadDetector(
        double ratioTimeConstantSeconds = 2.0,
        float vadOnThreshold = 0.55f,
        float vadOffThreshold = 0.40f)
    {
        if (vadOffThreshold >= vadOnThreshold)
            throw new ArgumentException(
                "vadOffThreshold must be < vadOnThreshold (hysteresis)");
        _vadOn = vadOnThreshold;
        _vadOff = vadOffThreshold;

        // Nominal STFT frame rate: hopSize(1024) / sampleRate(44100) ≈ 23 ms → ~43 Hz.
        const double frameRateHz = 40.0;
        _ratioAlpha = (float)(1.0 - Math.Exp(-1.0 / (frameRateHz * ratioTimeConstantSeconds)));
    }

    public void Reset()
    {
        _isVadOn = false;
        _speechMusicRatio = 0f;
    }

    public void ProcessContinuous(SpectralFrame frame, EventSink sink)
    {
        var mags = frame.Magnitudes;
        if (mags.Length == 0) return;

        // Total non-DC energy.
        double totalEnergy = 0.0;
        for (int i = 1; i < mags.Length; i++)
            totalEnergy += mags[i] * mags[i];

        if (totalEnergy < 1e-8)
        {
            // Silence: VAD off, ratio decays toward zero.
            _isVadOn = false;
            _speechMusicRatio += _ratioAlpha * (0f - _speechMusicRatio);
            sink.EmitContinuous("vad_state", 0f);
            sink.EmitContinuous("speech_music_ratio", _speechMusicRatio);
            return;
        }

        double binHz = (double)frame.SampleRate / frame.FftSize;
        int loBin = Math.Max(1, (int)Math.Floor(VoiceLoHz / binHz));
        int hiBinExcl = Math.Min(mags.Length, (int)Math.Ceiling(VoiceHiHz / binHz));

        // ── Feature 1: voice-band energy fraction ────────────────────
        double voiceEnergy = 0.0;
        for (int i = loBin; i < hiBinExcl; i++)
            voiceEnergy += mags[i] * mags[i];
        float voiceFraction = (float)(voiceEnergy / totalEnergy);
        float voiceScore = Smoothstep(0.30f, 0.65f, voiceFraction);

        // ── Feature 2: spectral centroid ────────────────────────────
        double weightedSum = 0.0;
        double sumMag = 0.0;
        double sumLog = 0.0;
        int count = 0;
        for (int i = 1; i < mags.Length; i++)
        {
            weightedSum += i * mags[i];
            sumMag += mags[i];
            sumLog += Math.Log(mags[i] + 1e-10);
            count++;
        }
        double centroidHz = (weightedSum / sumMag) * binHz;

        float centroidScore;
        if (centroidHz < CentroidLoHz)
            centroidScore = Smoothstep(150f, CentroidLoHz, (float)centroidHz);
        else if (centroidHz > CentroidHiHz)
            centroidScore = 1f - Smoothstep(CentroidHiHz, CentroidHiHz * 3f, (float)centroidHz);
        else
            centroidScore = 1f;

        // ── Feature 3: spectral flatness ────────────────────────────
        double geometricMean = Math.Exp(sumLog / count);
        double arithmeticMean = sumMag / count;
        float flatness = (float)(geometricMean / arithmeticMean);

        float flatnessScore;
        if (flatness < FlatnessLo)
            flatnessScore = Smoothstep(0f, FlatnessLo, flatness);
        else if (flatness > FlatnessHi)
            flatnessScore = 1f - Smoothstep(FlatnessHi, 0.9f, flatness);
        else
            flatnessScore = 1f;

        // ── Feature 4: zero-crossing rate ───────────────────────────
        float zcr = frame.ZeroCrossingRate;
        float zcrScore;
        if (zcr < ZcrLo)
            zcrScore = Smoothstep(0f, ZcrLo, zcr);
        else if (zcr > ZcrHi)
            zcrScore = 1f - Smoothstep(ZcrHi, ZcrHi * 2f, zcr);
        else
            zcrScore = 1f;

        // ── Aggregate: soft-min (AND-like, not brittle) ─────────────
        float minScore = Math.Min(
            Math.Min(voiceScore, centroidScore),
            Math.Min(flatnessScore, zcrScore));
        float avgScore = (voiceScore + centroidScore + flatnessScore + zcrScore) * 0.25f;
        float score = 0.7f * minScore + 0.3f * avgScore;

        // ── Hysteresis ──────────────────────────────────────────────
        if (_isVadOn && score < _vadOff) _isVadOn = false;
        else if (!_isVadOn && score > _vadOn) _isVadOn = true;

        // ── EMA of vad_state for section-level character ────────────
        float vad01 = _isVadOn ? 1f : 0f;
        _speechMusicRatio += _ratioAlpha * (vad01 - _speechMusicRatio);

        sink.EmitContinuous("vad_state", vad01);
        sink.EmitContinuous("speech_music_ratio", _speechMusicRatio);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (x <= edge0) return 0f;
        if (x >= edge1) return 1f;
        var t = (x - edge0) / (edge1 - edge0);
        return t * t * (3f - 2f * t);
    }
}
