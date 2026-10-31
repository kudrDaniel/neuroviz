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
        }

        // Вычисляем значения на основе _lastClock
        float currentPulse = _pulseParam?.EvaluateAt(_lastClock) ?? 0f;
        float currentShiver = _shiverParam?.EvaluateAt(_lastClock) ?? 0f;

        // Визуализация
        var baseColor = new Color(0.2f, 0.6f, 1.0f); // Синий
        var targetColor = new Color(1.0f, 0.5f, 0.0f); // Оранжевый

        var scale = 1.0f + currentPulse * 0.5f; // Пульс до 1.5x
        _rect.Scale = new Vector2(scale, scale);
        _rect.Color = baseColor.Lerp(targetColor, currentShiver);
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}