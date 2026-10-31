using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;
    private Label _statusLabel = null!;

    private ParamValue? _pulseParam;
    private ParamValue? _shiverParam;
    private ParamValue? _panParam;
    private ParamValue? _pitchParam;
    private ParamValue? _smoothParam;
    private ParamValue? _brightnessParam;

    private double _lastClock = 0.0; // <-- Запоминаем время последнего фрейма

    public override void _Ready()
    {
        _rect = new ColorRect
        {
            Color = new Color(0.2f, 0.6f, 1.0f),
            Size = new Vector2(100, 100),
            Position = new Vector2(150, 150),
            PivotOffset = new Vector2(50, 50),   // ← центр квадрата
        };
        AddChild(_rect);

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
            bool mmapOk = _ipc.GetAudioClockSeconds() != null;
            _statusLabel.Text = $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | " +
                                $"Params: {frame.Params.Count} | mmap: {(mmapOk ? "ok" : "off")}";

            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("shape_smooth", out var sh)) _smoothParam = sh;
            if (frame.Params.TryGetValue("brightness", out var b)) _brightnessParam = b;
        }

        // Часы: между IPC-фреймами (30 fps) берём свежее значение из mmap
        // (обновляется на каждый DSP-колбэк, ~10 мс) — затухание плавное.
        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;

        float currentPulse = _pulseParam?.EvaluateAt(nowClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPan = _panParam?.EvaluateAt(nowClock) ?? 0f;
        float currentPitch = _pitchParam?.EvaluateAt(nowClock) ?? 0f;
        float currentSmooth = _smoothParam?.EvaluateAt(nowClock) ?? 0f;
        float currentBrightness = _brightnessParam?.EvaluateAt(nowClock) ?? 0f;

        // Frame-rate independent smoothing: K = 25 → τ ≈ 40 мс.
        // 63% за 40 мс, 95% за 120 мс. "Snappy pop" вместо snap.
        float k = 1f - Mathf.Exp(-25f * (float)delta);

        // Панорама — теперь тоже независима от FPS
        var targetX = 150f + currentPan * 200f;
        _rect.Position = new Vector2(
            Mathf.Lerp(_rect.Position.X, targetX, k),
            _rect.Position.Y);

        // Пульс — плавный rise
        var targetScale = 1.0f + currentPulse * 0.5f;
        _rect.Scale = _rect.Scale.Lerp(new Vector2(targetScale, targetScale), k);

        // Гармоничность — плавное вращение
        _rect.Rotation = Mathf.Lerp(_rect.Rotation, currentSmooth * 0.785f, k);

        // Цвет — единый конвейер + сглаживание на выходе
        var targetColor = new Color(0.2f, 0.6f, 1.0f);
        targetColor = targetColor.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);
        targetColor = targetColor.Lerp(Colors.White, currentBrightness * 0.5f);
        targetColor = targetColor.Lerp(Colors.Orange, currentShiver);
        _rect.Color = _rect.Color.Lerp(targetColor, k);
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}