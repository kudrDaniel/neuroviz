using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;       // event-driven (existing)
    private ColorRect _contRect = null!;   // continuous-driven (new, proves B2)
    private Label _statusLabel = null!;

    // Event-driven params (decay via EvaluateAt)
    private ParamValue? _pulseParam;
    private ParamValue? _shiverParam;
    private ParamValue? _panParam;
    private ParamValue? _pitchParam;
    // NOTE: shape_smooth/brightness were event-driven rules; now continuous.

    // Continuous params (direct, no decay)
    private float _contHarmonicity;
    private float _contCentroid;
    private float _smoothHarmonicity;
    private float _smoothCentroid;

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

        // Continuous-driven indicator. Scale follows spectral_centroid
        // directly (no events). This is B2 acceptance: a continuous value
        // from the pipeline drives a visual property smoothly.
        _contRect = new ColorRect
        {
            Color = new Color(1.0f, 0.5f, 0.2f, 0.7f),
            Size = new Vector2(60, 60),
            Position = new Vector2(400, 300),
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

            // ── Event params (with decay) ─────────────────────────────
            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;

            // ── Continuous params (no decay, direct) ──────────────────
            // Empty dict in replay → keep last values (see KNOWN_ISSUES.md).
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var h))
                _contHarmonicity = h;
            if (frame.ContinuousParams.TryGetValue("spectral_centroid", out var c))
                _contCentroid = c;

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

        // Frame-rate independent smoothing.
        // k=25 (τ≈40ms) for event-driven; kc=15 (τ≈67ms) for continuous —
        // a bit slower to smooth per-frame jitter in raw spectral metrics.
        float k = 1f - Mathf.Exp(-25f * (float)delta);
        float kc = 1f - Mathf.Exp(-15f * (float)delta);

        // Continuous smoothing (jitter reduction)
        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kc);
        _smoothCentroid = Mathf.Lerp(_smoothCentroid, _contCentroid, kc);

        // ── Existing event-driven rect ───────────────────────────────
        var targetX = 150f + currentPan * 200f;
        _rect.Position = new Vector2(
            Mathf.Lerp(_rect.Position.X, targetX, k),
            _rect.Position.Y);

        var targetScale = 1.0f + currentPulse * 0.5f;
        _rect.Scale = _rect.Scale.Lerp(new Vector2(targetScale, targetScale), k);

        // shape_smooth: was a rule action on "harmonicity" event; now direct.
        _rect.Rotation = Mathf.Lerp(_rect.Rotation, _smoothHarmonicity * 0.785f, k);

        // Color: brightness from continuous centroid, was a rule on
        // "spectral_centroid" event; now direct.
        var targetColor = new Color(0.2f, 0.6f, 1.0f);
        targetColor = targetColor.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);
        targetColor = targetColor.Lerp(Colors.White, _smoothCentroid * 0.5f);
        targetColor = targetColor.Lerp(Colors.Orange, currentShiver);
        _rect.Color = _rect.Color.Lerp(targetColor, k);

        // ── Continuous-driven rect (B2 acceptance demo) ──────────────
        // Scale tracks spectral_centroid continuously — no events involved.
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