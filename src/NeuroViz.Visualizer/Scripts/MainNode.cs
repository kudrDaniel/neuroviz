using Godot;
using NeuroViz.Core.Ipc;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode : Node2D
{
    private IpcClient _ipc = null!;
    private ColorRect _rect = null!;
    private Label _statusLabel = null!;

    // Храним последнее полученное значение параметра
    private ParamValue? _pulseParam;
    private ParamValue? _shiverParam;

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
            // Используем время из фрейма, а не из mmap
            double clock = frame.AudioClockSeconds;
            _statusLabel.Text = $"Clock: {clock:F2}s | Frame: {frame.FrameSeq} | Params: {frame.Params.Count}";

            // Обновляем параметры, если пришли новые
            if (frame.Params.TryGetValue("pulse", out var p)) _pulseParam = p;
            if (frame.Params.TryGetValue("shiver", out var s)) _shiverParam = s;
        }

        // Вычисляем текущее значение параметра с учетом затухания
        float currentPulse = 0f;
        float currentShiver = 0f;
        double currentClock = frame?.AudioClockSeconds ?? 0;

        if (_pulseParam != null)
            currentPulse = _pulseParam.EvaluateAt(currentClock);

        if (_shiverParam != null)
            currentShiver = _shiverParam.EvaluateAt(currentClock);

        // Визуализация
        var scale = 1.0f + currentPulse * 0.5f; // Квадрат увеличивается до 1.5x
        _rect.Scale = new Vector2(scale, scale);
        _rect.Color = new Color(0.2f, 0.6f, 1.0f).Lerp(Colors.Orange, currentShiver);
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}