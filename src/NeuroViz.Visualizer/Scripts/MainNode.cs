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
            _statusLabel.Text = $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | Params: {frame.Params.Count}";

            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("shape_smooth", out var sh)) _smoothParam = sh;
            if (frame.Params.TryGetValue("brightness", out var b)) _brightnessParam = b;
        }

        float currentPulse = _pulseParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentPan = _panParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentPitch = _pitchParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentSmooth = _smoothParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentBrightness = _brightnessParam?.EvaluateAt(_lastClock) ?? 0f;

        // 1. Панорама — смещение по X (плавно)
        var targetX = 150f + (currentPan * 200f);
        _rect.Position = new Vector2(Mathf.Lerp(_rect.Position.X, targetX, 0.1f), _rect.Position.Y);

        // 2. Пульс — масштаб (до 1.5×)
        var scale = 1.0f + currentPulse * 0.5f;
        _rect.Scale = new Vector2(scale, scale);

        // 3. Гармоничность — вращение (0..45°)
        _rect.Rotation = currentSmooth * 0.785f;

        // 4. Цвет — ЕДИНЫЙ конвейер: base → pitch → brightness → shiver
        var color = new Color(0.2f, 0.6f, 1.0f);                              // базовый синий
        color = color.Lerp(new Color(0.8f, 0.9f, 1.0f), currentPitch);        // высота тона
        color = color.Lerp(Colors.White, currentBrightness * 0.5f);           // яркость
        color = color.Lerp(Colors.Orange, currentShiver);                     // новизна
        _rect.Color = color;
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}