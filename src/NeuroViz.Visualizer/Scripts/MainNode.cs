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
            Position = new Vector2(150, 150)
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
            _lastClock = frame.AudioClockSeconds; // <-- Обновляем часы ТОЛЬКО когда пришел фрейм
            _statusLabel.Text = $"Clock: {_lastClock:F2}s | Frame: {frame.FrameSeq} | Params: {frame.Params.Count}";

            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
            if (frame.Params.TryGetValue("pan_x", out var pan)) _panParam = pan;
            if (frame.Params.TryGetValue("pitch_height", out var pitch)) _pitchParam = pitch;
            if (frame.Params.TryGetValue("shape_smooth", out var sh)) _smoothParam = sh;
            if (frame.Params.TryGetValue("brightness", out var b)) _brightnessParam = b;
        }

        // Вычисляем значения на основе _lastClock
        float currentPulse = _pulseParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(_lastClock) ?? 0f;

        float currentPan = _panParam?.EvaluateAt(_lastClock) ?? 0f; // -1..1
        float currentPitch = _pitchParam?.EvaluateAt(_lastClock) ?? 0f; // 0..1

        float currentSmooth = _smoothParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentBrightness = _brightnessParam?.EvaluateAt(_lastClock) ?? 0f;

        // Смещаем по X (панорама)
        var targetX = 150f + (currentPan * 200f); // Сдвиг на +/- 200 пикселей
        _rect.Position = new Vector2(Mathf.Lerp(_rect.Position.X, targetX, 0.1f), _rect.Position.Y);

        // Меняем насыщенность (Pitch)
        var baseColor = new Color(0.2f, 0.6f, 1.0f);
        var pitchColor = new Color(0.8f, 0.9f, 1.0f); // Более яркий/белый для высоких нот
        var mixedColor = baseColor.Lerp(pitchColor, currentPitch);

        // Вращение от гармоничности (0 рад ... 0.785 рад (45 градусов))
        _rect.Rotation = currentSmooth * 0.785f;

        // Яркость (свечение)
        var finalColor = _rect.Color.Lerp(Colors.White, currentBrightness * 0.5f);
        _rect.Color = finalColor;

        var scale = 1.0f + currentPulse * 0.5f; // Пульс до 1.5x
        _rect.Scale = new Vector2(scale, scale);
        _rect.Color = mixedColor.Lerp(Colors.Orange, currentShiver);
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}