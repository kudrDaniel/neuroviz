using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;         // event-driven
    private ColorRect _contRect = null!;     // spectral_centroid-driven (B2)
    private ColorRect _bandRect = null!;     // band_energy-driven (B3)
    private Label _statusLabel = null!;

    // Event-driven params (decay via EvaluateAt)
    private ParamValue? _pulseParam;
    private ParamValue? _releaseParam;
    private ParamValue? _shiverParam;
    private ParamValue? _panParam;
    private ParamValue? _pitchParam;

    // Continuous params (direct, no decay)
    private float _contHarmonicity;
    private float _contCentroid;
    private float _contLowEnergy;
    private float _contMidEnergy;
    private float _contHighEnergy;

    // Smoothed continuous values (frame-rate independent lerp)
    private float _smoothHarmonicity;
    private float _smoothCentroid;
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

        // B2 acceptance: scale from spectral_centroid, color from harmonicity.
        _contRect = new ColorRect
        {
            Color = new Color(1.0f, 0.5f, 0.2f, 0.7f),
            Size = new Vector2(60, 60),
            Position = new Vector2(400, 300),
            PivotOffset = new Vector2(30, 30),
        };
        AddChild(_contRect);

        // B3 acceptance: scale from band_energy.low, emissive tint from
        // band_energy.high, vertical position nudged by band_energy.mid.
        _bandRect = new ColorRect
        {
            Color = new Color(0.3f, 0.4f, 1.0f, 0.8f),
            Size = new Vector2(80, 80),
            Position = new Vector2(600, 400),
            PivotOffset = new Vector2(40, 40),
        };
        AddChild(_bandRect);

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

            // ── Event params (with decay) ─────────────────────────────
            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("release", out var rel)) _releaseParam = rel;

            // ── Continuous params (no decay, direct) ──────────────────
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var h))
                _contHarmonicity = h;
            if (frame.ContinuousParams.TryGetValue("spectral_centroid", out var c))
                _contCentroid = c;
            if (frame.ContinuousParams.TryGetValue("band_energy.low", out var lo))
                _contLowEnergy = lo;
            if (frame.ContinuousParams.TryGetValue("band_energy.mid", out var md))
                _contMidEnergy = md;
            if (frame.ContinuousParams.TryGetValue("band_energy.high", out var hi))
                _contHighEnergy = hi;

            bool mmapOk = _ipc.GetAudioClockSeconds() != null;
            _statusLabel.Text =
                $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | " +
                $"Params: {frame.Params.Count} | Cont: {frame.ContinuousParams.Count} | " +
                $"mmap: {(mmapOk ? "ok" : "off")}";
        }

        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;

        // Event-driven currents (existing, unchanged)
        float currentPulse = _pulseParam?.EvaluateAt(nowClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPan = _panParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPitch = _pitchParam?.EvaluateAt(nowClock) ?? 0f;
        float currentRelease = _releaseParam?.EvaluateAt(nowClock) ?? 0f;

        // Frame-rate independent smoothing.
        // Event-driven: k=25 (τ≈40ms), continuous: kc=15 (τ≈67ms).
        float k = 1f - Mathf.Exp(-25f * (float)delta);
        float kc = 1f - Mathf.Exp(-15f * (float)delta);

        // Continuous smoothing
        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kc);
        _smoothCentroid = Mathf.Lerp(_smoothCentroid, _contCentroid, kc);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLowEnergy, kc);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMidEnergy, kc);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHighEnergy, kc);

        // ── Existing event-driven rect ───────────────────────────────
        var targetX = 150f + currentPan * 200f;
        _rect.Position = new Vector2(
            Mathf.Lerp(_rect.Position.X, targetX, k),
            _rect.Position.Y);

        // pulse: onset blows up; release: offset shrinks — a rhythmic in/out.
        // Clip to a sane minimum so a strong simultaneous pulse+release cannot
        // flip the square to negative scale.
        var targetScale = Mathf.Max(0.3f, 1.0f + currentPulse * 0.5f - currentRelease * 0.35f);
        _rect.Scale = _rect.Scale.Lerp(new Vector2(targetScale, targetScale), k);

        _rect.Rotation = Mathf.Lerp(_rect.Rotation, _smoothHarmonicity * 0.785f, k);

        var targetColor = new Color(0.2f, 0.6f, 1.0f);
        targetColor = targetColor.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);
        targetColor = targetColor.Lerp(Colors.White, _smoothCentroid * 0.5f);
        targetColor = targetColor.Lerp(Colors.Orange, currentShiver);
        _rect.Color = _rect.Color.Lerp(targetColor, k);

        // ── B2 continuous-driven rect (spectral_centroid) ────────────
        var contScale = 0.3f + _smoothCentroid * 2.5f;
        _contRect.Scale = new Vector2(contScale, contScale);
        _contRect.Color = new Color(1.0f, 0.5f, 0.2f, 0.7f)
            .Lerp(new Color(0.2f, 1.0f, 0.5f, 0.9f), _smoothHarmonicity);

        // ── B3 continuous-driven rect (band_energy) ──────────────────
        // Scale from low-band energy: kicks and bass swell make it big,
        // vocals/cymbals-only sections make it small.
        var bandScale = 0.4f + _smoothLow * 3.0f;
        _bandRect.Scale = new Vector2(bandScale, bandScale);

        // Emissive tint from high-band energy: hats, cymbals, sibilants.
        _bandRect.Color = new Color(0.3f, 0.4f, 1.0f, 0.8f)
            .Lerp(new Color(1.0f, 0.9f, 0.3f, 0.95f), _smoothHigh);

        // Vertical position nudged by mid-band energy (subtle secondary channel).
        var targetY = 400f - _smoothMid * 80f;
        _bandRect.Position = new Vector2(
            _bandRect.Position.X,
            Mathf.Lerp(_bandRect.Position.Y, targetY, kc));
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}