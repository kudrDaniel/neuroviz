using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;        // music layer (events + band_energy + flux)
    private ColorRect _vocalRect = null!;   // voice layer (C4, continuous only)
    private ColorRect _contRect = null!;    // B2 acceptance demo
    private Label _statusLabel = null!;

    // ── Event params ────────────────────────────────────────────────
    private ParamValue? _pulseParam;
    private ParamValue? _shiverParam;
    private ParamValue? _panParam;
    private ParamValue? _pitchParam;
    private ParamValue? _releaseParam;
    private ParamValue? _streamGlowParam;
    private ParamValue? _streamFadeParam;

    // ── Continuous: B2 (spectral features) ──────────────────────────
    private float _contHarmonicity, _contCentroid;
    private float _smoothHarmonicity, _smoothCentroid;

    // ── Continuous: B3 (band energy) ────────────────────────────────
    private float _contLow, _contMid, _contHigh;
    private float _smoothLow, _smoothMid, _smoothHigh;

    // ── Continuous: C3 (spectral flux) ──────────────────────────────
    private float _contFlux, _smoothFlux;

    // ── Continuous: C1 (VAD) ────────────────────────────────────────
    private float _contSpeechMusicRatio, _smoothSpeechMusicRatio;

    private double _lastClock = 0.0;

    public override void _Ready()
    {
        _rect = new ColorRect
        {
            Color = new Color(0.2f, 0.6f, 1.0f),
            Size = new Vector2(100, 100),
            Position = new Vector2(150, 150),
            PivotOffset = new Vector2(50, 50),
        };
        AddChild(_rect);

        // Voice layer: starts fully transparent (ratio = 0 at startup,
        // so vocal alpha = 0). Alpha cross-faded by speech_music_ratio.
        _vocalRect = new ColorRect
        {
            Color = new Color(0.3f, 1.0f, 0.5f, 0f),
            Size = new Vector2(80, 80),
            Position = new Vector2(400, 160),
            PivotOffset = new Vector2(40, 40),
        };
        AddChild(_vocalRect);

        // B2 acceptance demo — unchanged, docs that the continuous path
        // works alongside the new two-layer scene.
        _contRect = new ColorRect
        {
            Color = new Color(1.0f, 0.5f, 0.2f, 0.7f),
            Size = new Vector2(60, 60),
            Position = new Vector2(650, 400),
            PivotOffset = new Vector2(30, 30),
        };
        AddChild(_contRect);

        _statusLabel = new Label { Position = new Vector2(10, 10) };
        AddChild(_statusLabel);

        _ipc = new IpcClient();
        _ipc.Start();
    }

    public override void _Process(double delta)
    {
        _ipc.DrainMainThreadActions();

        if (!_ipc.IsConnected)
        {
            _statusLabel.Text = "Waiting for Avalonia...";
            return;
        }

        if (_ipc.PendingScenePath is { } pendingPath)
        {
            _ipc.ClearPendingScene();
            GetTree().ChangeSceneToFile(pendingPath);
            return;
        }

        var frame = _ipc.GetLatestFrame();

        if (frame != null)
        {
            _lastClock = frame.AudioClockSeconds;

            // Event params
            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("release", out var rel)) _releaseParam = rel;
            if (frame.Params.TryGetValue("stream_glow", out var glow)) _streamGlowParam = glow;
            if (frame.Params.TryGetValue("stream_fade", out var fade)) _streamFadeParam = fade;

            // Continuous: B2
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var h)) _contHarmonicity = h;
            if (frame.ContinuousParams.TryGetValue("spectral_centroid", out var c)) _contCentroid = c;

            // Continuous: B3
            if (frame.ContinuousParams.TryGetValue("band_energy.low", out var bl)) _contLow = bl;
            if (frame.ContinuousParams.TryGetValue("band_energy.mid", out var bm)) _contMid = bm;
            if (frame.ContinuousParams.TryGetValue("band_energy.high", out var bh)) _contHigh = bh;

            // Continuous: C3
            if (frame.ContinuousParams.TryGetValue("spectral_flux", out var f)) _contFlux = f;

            // Continuous: C1
            if (frame.ContinuousParams.TryGetValue("speech_music_ratio", out var r)) _contSpeechMusicRatio = r;

            bool mmapOk = _ipc.GetAudioClockSeconds() != null;
            _statusLabel.Text =
                $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | " +
                $"Params: {frame.Params.Count} | Cont: {frame.ContinuousParams.Count} | " +
                $"Flux: {_smoothFlux:F2} | Ratio: {_smoothSpeechMusicRatio:F2} | " +
                $"mmap: {(mmapOk ? "ok" : "off")}";
        }

        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;

        // ── Event-driven currents ────────────────────────────────────
        float currentPulse = _pulseParam?.EvaluateAt(nowClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPan = _panParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPitch = _pitchParam?.EvaluateAt(nowClock) ?? 0f;
        float currentRelease = _releaseParam?.EvaluateAt(nowClock) ?? 0f;
        float currentGlow = _streamGlowParam?.EvaluateAt(nowClock) ?? 0f;
        float currentFade = _streamFadeParam?.EvaluateAt(nowClock) ?? 0f;

        // ── Smoothing ────────────────────────────────────────────────
        float k = 1f - Mathf.Exp(-25f * (float)delta);  // events (~40 ms)
        float kc = 1f - Mathf.Exp(-15f * (float)delta);  // spectral (~67 ms)
        float kb = 1f - Mathf.Exp(-12f * (float)delta);  // band_energy (~83 ms)
        float kf = 1f - Mathf.Exp(-30f * (float)delta);  // flux (~33 ms)
        // Ratio is already EMA-smoothed in VadDetector (τ ≈ 2 s); kc on
        // top just removes residual per-tick jitter without adding lag.
        float kr = kc;

        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kc);
        _smoothCentroid = Mathf.Lerp(_smoothCentroid, _contCentroid, kc);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMid, kb);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHigh, kb);
        _smoothFlux = Mathf.Lerp(_smoothFlux, _contFlux, kf);
        _smoothSpeechMusicRatio = Mathf.Lerp(
            _smoothSpeechMusicRatio, _contSpeechMusicRatio, kr);

        // Global brightness modifier from spectral_flux (C3) — shared by
        // both layers so the whole scene flashes on attacks.
        float fluxBrightness = 0.7f + Mathf.Min(0.3f, _smoothFlux * 1.5f);

        // ── Music layer (_rect) ──────────────────────────────────────
        var targetX = 150f + currentPan * 200f;
        _rect.Position = new Vector2(
            Mathf.Lerp(_rect.Position.X, targetX, k),
            _rect.Position.Y);

        var targetScale = Mathf.Max(0.3f,
            1.0f
            + currentPulse * 0.5f
            - currentRelease * 0.35f
            + _smoothLow * 0.4f);
        _rect.Scale = _rect.Scale.Lerp(new Vector2(targetScale, targetScale), k);

        _rect.Rotation = Mathf.Lerp(_rect.Rotation, _smoothHarmonicity * 0.785f, k);

        var musicColor = new Color(0.2f, 0.6f, 1.0f);                              // base blue
        musicColor = musicColor.Lerp(new Color(0.4f, 1.0f, 0.7f), _smoothMid * 0.4f); // mid → teal
        musicColor = musicColor.Lerp(Colors.White, _smoothHigh * 0.5f);            // high → white
        musicColor = musicColor.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);   // pitch → light blue
        musicColor = musicColor.Lerp(Colors.Orange, currentShiver);                // shiver → orange
        musicColor = musicColor.Lerp(Colors.White, currentGlow * 0.6f);            // stream_glow
        musicColor = musicColor.Lerp(new Color(0.3f, 0.3f, 0.3f), currentFade * 0.5f); // stream_fade
        musicColor = new Color(
            musicColor.R * fluxBrightness,
            musicColor.G * fluxBrightness,
            musicColor.B * fluxBrightness);

        _rect.Color = _rect.Color.Lerp(musicColor, k);

        // ── Voice layer (_vocalRect) — continuous only ───────────────
        // No pulse / release / pan: voice does not pulse on a kick drum.
        var vocalScale = 0.6f + _smoothCentroid * 1.2f;
        _vocalRect.Scale = _vocalRect.Scale.Lerp(new Vector2(vocalScale, vocalScale), kc);
        _vocalRect.Rotation = Mathf.Lerp(_vocalRect.Rotation, _smoothHarmonicity * 0.5f, kc);

        var vocalColor = new Color(0.3f, 1.0f, 0.5f);                              // base green
        vocalColor = vocalColor.Lerp(new Color(0.5f, 1.0f, 0.3f), _smoothMid * 0.3f); // slight hue shift
        vocalColor = vocalColor.Lerp(Colors.White, _smoothHigh * 0.4f);
        vocalColor = vocalColor.Lerp(Colors.White, currentGlow * 0.6f);
        vocalColor = vocalColor.Lerp(new Color(0.3f, 0.3f, 0.3f), currentFade * 0.5f);
        vocalColor = new Color(
            vocalColor.R * fluxBrightness,
            vocalColor.G * fluxBrightness,
            vocalColor.B * fluxBrightness);

        _vocalRect.Color = _vocalRect.Color.Lerp(vocalColor, kc);

        // ── Cross-fade alpha ─────────────────────────────────────────
        // Applied AFTER the color Lerp, otherwise target alpha=1 would
        // pull the rectangle back to opaque each frame.
        float vocalAlpha = Mathf.Clamp(_smoothSpeechMusicRatio, 0f, 1f);
        float musicAlpha = 1f - vocalAlpha;

        _rect.Color = new Color(_rect.Color.R, _rect.Color.G, _rect.Color.B, musicAlpha);
        _vocalRect.Color = new Color(_vocalRect.Color.R, _vocalRect.Color.G, _vocalRect.Color.B, vocalAlpha);

        // ── B2 acceptance demo (unchanged) ───────────────────────────
        var contScale = 0.3f + _smoothCentroid * 2.5f;
        _contRect.Scale = new Vector2(contScale, contScale);
        _contRect.Color = new Color(1.0f, 0.5f, 0.2f, 0.7f)
            .Lerp(new Color(0.2f, 1.0f, 0.5f, 0.9f), _smoothHarmonicity);

        // P2-A: preview capture (works in 2D too — same rules).
        double now = Time.GetTicksMsec() / 1000.0;
        if (_ipc.ShouldCapturePreview(now))
            CapturePreviewAfterDraw();
    }

    private bool _previewFirstCaptureLogged;

    private async void CapturePreviewAfterDraw()
    {
        // GetTexture().GetImage() called from _Process reads the viewport
        // before the current frame is drawn by the GPU — in GL compatibility
        // mode this returns black reliably. Defer to FramePostDraw: by that
        // signal the GPU has finished writing to the viewport texture and
        // GetImage() returns real pixels.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        // Window may have been closed while awaiting.
        if (!IsInsideTree() || !_ipc.IsPreviewConnected) return;

        var img = GetViewport().GetTexture().GetImage();
        if (img == null || img.IsEmpty())
        {
            GD.PrintErr("[Preview] GetImage returned null/empty");
            return;
        }

        // One-shot diagnostic: sample a sparse grid for any non-black pixel.
        // If this logs non-black=0, capture is broken beyond the timing fix
        // (likely renderer/driver); tell us and we switch to Forward+.
        if (!_previewFirstCaptureLogged)
        {
            _previewFirstCaptureLogged = true;
            int nonBlack = 0;
            for (int y = 0; y < img.GetHeight(); y += 32)
                for (int x = 0; x < img.GetWidth(); x += 32)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 0.02f || c.G > 0.02f || c.B > 0.02f) nonBlack++;
                }
            GD.Print($"[Preview] First capture {img.GetWidth()}x{img.GetHeight()}, non-black samples: {nonBlack}");
        }

        img.Resize(320, 180, Image.Interpolation.Bilinear);
        _ipc.QueuePreview(img);
        img.Dispose();
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}