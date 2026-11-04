using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;        // main visual: events + band_energy
    private ColorRect _contRect = null!;    // B2 acceptance demo: centroid + harmonicity
    private Label _statusLabel = null!;

    // Event-driven params (decay via EvaluateAt)
    private ParamValue? _pulseParam;
    private ParamValue? _shiverParam;
    private ParamValue? _panParam;
    private ParamValue? _pitchParam;
    private ParamValue? _releaseParam;

    // Continuous: B2 (spectral features)
    private float _contHarmonicity;
    private float _contCentroid;
    private float _smoothHarmonicity;
    private float _smoothCentroid;

    // Continuous: B3 (band energy)
    private float _contLow;
    private float _contMid;
    private float _contHigh;
    private float _smoothLow;
    private float _smoothMid;
    private float _smoothHigh;

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

        // B2 acceptance demo — kept from B2 to prove continuous path still
        // works alongside the new band_energy channels below.
        _contRect = new ColorRect
        {
            Color = new Color(1.0f, 0.5f, 0.2f, 0.7f),
            Size = new Vector2(60, 60),
            Position = new Vector2(500, 400),
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
        if (!_ipc.IsConnected)
        {
            _statusLabel.Text = "Waiting for Avalonia...";
            return;
        }

        var frame = _ipc.GetLatestFrame();

        if (frame != null)
        {
            _lastClock = frame.AudioClockSeconds;

            // ── Event params ─────────────────────────────────────────
            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("release", out var rel)) _releaseParam = rel;

            // ── Continuous params (B2) ───────────────────────────────
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var h)) _contHarmonicity = h;
            if (frame.ContinuousParams.TryGetValue("spectral_centroid", out var c)) _contCentroid = c;

            // ── Continuous params (B3) ───────────────────────────────
            if (frame.ContinuousParams.TryGetValue("band_energy.low", out var bl)) _contLow = bl;
            if (frame.ContinuousParams.TryGetValue("band_energy.mid", out var bm)) _contMid = bm;
            if (frame.ContinuousParams.TryGetValue("band_energy.high", out var bh)) _contHigh = bh;

            bool mmapOk = _ipc.GetAudioClockSeconds() != null;
            _statusLabel.Text =
                $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | " +
                $"Params: {frame.Params.Count} | Cont: {frame.ContinuousParams.Count} | " +
                $"mmap: {(mmapOk ? "ok" : "off")}";
        }

        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;

        // ── Event-driven currents ────────────────────────────────────
        float currentPulse = _pulseParam?.EvaluateAt(nowClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPan = _panParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPitch = _pitchParam?.EvaluateAt(nowClock) ?? 0f;
        float currentRelease = _releaseParam?.EvaluateAt(nowClock) ?? 0f;

        // ── Smoothing ────────────────────────────────────────────────
        // k  = τ ≈ 40 ms — event params (snappy pop).
        // kc = τ ≈ 67 ms — spectral features (B2).
        // kb = τ ≈ 83 ms — band energy (B3). Slightly slower so per-frame
        //   jitter doesn't fight the "breathing"; still fast enough to track
        //   a kick (~200-500 ms period) without visible lag.
        float k = 1f - Mathf.Exp(-25f * (float)delta);
        float kc = 1f - Mathf.Exp(-15f * (float)delta);
        float kb = 1f - Mathf.Exp(-12f * (float)delta);

        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kc);
        _smoothCentroid = Mathf.Lerp(_smoothCentroid, _contCentroid, kc);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMid, kb);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHigh, kb);

        // ── Main rect: events + band_energy ──────────────────────────
        // Pan (event) → horizontal position.
        var targetX = 150f + currentPan * 200f;
        _rect.Position = new Vector2(
            Mathf.Lerp(_rect.Position.X, targetX, k),
            _rect.Position.Y);

        // Scale = base (1.0) + onset blow-up + offset release + band_energy.low
        // breathing. Clipped at 0.3 to keep pulse+release combinations sane.
        var targetScale = Mathf.Max(0.3f,
            1.0f
            + currentPulse * 0.5f
            - currentRelease * 0.35f
            + _smoothLow * 0.4f);
        _rect.Scale = _rect.Scale.Lerp(new Vector2(targetScale, targetScale), k);

        // Rotation from harmonicity (B2 continuous, kept for character).
        _rect.Rotation = Mathf.Lerp(_rect.Rotation, _smoothHarmonicity * 0.785f, k);

        // Color pipeline (B5 roadmap: three channels from three bands):
        //   band_energy.mid  → hue shift (blue → teal)
        //   band_energy.high → brightness (towards white)
        //   pitch / shiver   → event-driven accents layered on top
        var targetColor = new Color(0.2f, 0.6f, 1.0f);                                  // base blue
        targetColor = targetColor.Lerp(new Color(0.4f, 1.0f, 0.7f), _smoothMid * 0.4f); // mid → teal
        targetColor = targetColor.Lerp(Colors.White, _smoothHigh * 0.5f);                // high → white
        targetColor = targetColor.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);       // pitch → light blue
        targetColor = targetColor.Lerp(Colors.Orange, currentShiver);                    // shiver → orange
        _rect.Color = _rect.Color.Lerp(targetColor, k);

        // ── Continuous demo rect (B2 acceptance, unchanged) ─────────
        var contScale = 0.3f + _smoothCentroid * 2.5f;
        _contRect.Scale = new Vector2(contScale, contScale);
        _contRect.Color = new Color(1.0f, 0.5f, 0.2f, 0.7f)
            .Lerp(new Color(0.2f, 1.0f, 0.5f, 0.9f), _smoothHarmonicity);
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}