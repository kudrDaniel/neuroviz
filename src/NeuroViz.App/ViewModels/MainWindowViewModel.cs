using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Audio;
using NeuroViz.Core.Events;
using NeuroViz.Project;
using NeuroViz.App.Ipc;
using NeuroViz.Core.Ipc;
using NeuroViz.Core.Rules;

namespace NeuroViz.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AudioClockWriter _clock;
    private readonly ManagedBassAudioPlayer _player;
    private readonly DispatcherTimer _timer;
    private readonly IpcServer _ipcServer;
    private readonly RuleEngine _ruleEngine;
    private readonly RulesDocument _rulesDocument;
    private AnalyzerPipeline? _pipeline;
    private bool _disposed;
    private bool _isLiveAnalysis = true;
    private double _lastTickPosition = 0;

    private readonly CancellationTokenSource _lifetimeCts = new();

    public bool IsDragging { get; set; }

    public ObservableCollection<EventRow> Events { get; } = new();

    private string? _currentFilePath;
    public string? CurrentFilePath
    {
        get => _currentFilePath;
        set => SetProperty(ref _currentFilePath, value);
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    private double _position;
    public double Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    private double _sliderPosition;
    public double SliderPosition
    {
        get => _sliderPosition;
        set => SetProperty(ref _sliderPosition, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    private long _detectorErrors;
    public long DetectorErrors
    {
        get => _detectorErrors;
        set => SetProperty(ref _detectorErrors, value);
    }

    private string _statusText = "No file loaded";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _isDebugMode;
    public bool IsDebugMode
    {
        get => _isDebugMode;
        set => SetProperty(ref _isDebugMode, value);
    }

    public AppSettings Settings { get; }

    public MainWindowViewModel()
    {
        Settings = AppSettings.Load();

        _clock = new AudioClockWriter();
        _player = new ManagedBassAudioPlayer(_clock);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _ipcServer = new IpcServer();

        var rulesPath = ResolveRulesPath();
        RulesDocument rulesDoc;
        if (rulesPath is not null)
        {
            try { rulesDoc = RulesLoader.FromFile(rulesPath); }
            catch (Exception ex)
            {
                StatusText = $"Rules load failed ({Path.GetFileName(rulesPath)}): {ex.Message}";
                rulesDoc = RulesLoader.FromJson("{ \"version\": 1, \"rules\": [] }");
            }
        }
        else
        {
            StatusText = "Rules file not found; using empty rule set";
            rulesDoc = RulesLoader.FromJson("{ \"version\": 1, \"rules\": [] }");
        }

        _rulesDocument = rulesDoc;
        _ruleEngine = new RuleEngine(_rulesDocument);
    }

    private string? ResolveRulesPath()
    {
        // 1. Явный путь из настроек (для кастомных правил пользователя).
        if (!string.IsNullOrEmpty(Settings.RulesPath) && File.Exists(Settings.RulesPath))
            return Settings.RulesPath;

        // 2. Файл рядом с exe (копируется через csproj, работает и в publish).
        var bundled = Path.Combine(AppContext.BaseDirectory, "samples", "rules", "basic.json");
        if (File.Exists(bundled)) return bundled;

        // 3. Dev-fallback: источник в дереве репозитория (F5 без сборки App-проекта).
        var dev = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "samples", "rules", "basic.json"));
        if (File.Exists(dev)) return dev;

        return null;
    }

    private readonly HashSet<(string Type, long TickMs)> _eventKeys = new();

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        if (!IsDragging)
        {
            Position = _player.Position;
            SliderPosition = Position;
        }
        IsPlaying = _player.IsPlaying;
        DetectorErrors = _pipeline?.DetectorErrors ?? 0;

        if (!IsPlaying) return;

        var paramValues = new Dictionary<string, ParamValue>();
        List<AudioEvent> eventsToSend;
        double currentPos;

        if (_isLiveAnalysis && _pipeline != null)
        {
            currentPos = _pipeline.LastAnalyzedTimestamp;
            var batch = _pipeline.Sink.DrainAll();
            eventsToSend = new List<AudioEvent>(batch.Count);

            foreach (var analyzed in batch)
            {
                // O(1) dedup: (Type, timestamp rounded to 1 ms) key.
                var key = (analyzed.Event.Type, ToTickMs(analyzed.Event.TimestampSeconds));
                if (_eventKeys.Add(key))
                    Events.Add(EventRow.From(analyzed));
                eventsToSend.Add(analyzed.Event);
            }
        }
        else
        {
            currentPos = _player.Position;
            var windowStart = _lastTickPosition - 0.05;
            eventsToSend = Events
                .Where(r => r.Timestamp > windowStart && r.Timestamp <= currentPos)
                .Select(r => r.Source)
                .ToList();
        }

        // Приоритеты: внутри одного события — побеждает высший приоритет
        // (Process отдаёт действия в порядке убывания priority); между событиями
        // — побеждает более позднее (естественное течение времени).
        foreach (var ev in eventsToSend)
        {
            var paramsInThisEvent = new HashSet<string>();
            foreach (var action in _ruleEngine.Process(ev, ev.TimestampSeconds))
            {
                if (!paramsInThisEvent.Add(action.Param))
                    continue;

                paramValues[action.Param] = new ParamValue
                {
                    Value = action.Value,
                    SetAtSeconds = action.AtSeconds,
                    DecayMs = action.DecayMs
                };
            }
        }

        _ipcServer.PushFrame(currentPos, eventsToSend, paramValues);
        _lastTickPosition = currentPos;
    }

    public async Task LoadFileAsync(string path)
    {
        try
        {
            StopPlayback();
            Events.Clear();
            _eventKeys.Clear();
            _pipeline?.Dispose();
            _pipeline = null;

            _player.Load(path);
            Duration = _player.Duration;
            CurrentFilePath = path;

            _pipeline = new AnalyzerPipeline(_player.SampleRate, _player.Channels);
            _pipeline.AddDetector(new OnsetDetector(historySize: 20, thresholdMult: 2.2f, refractorySeconds: 0.07));
            _pipeline.AddDetector(new NoveltyDetector(alpha: 0.01f, historySize: 50, thresholdMult: 2.5f, refractorySeconds: 1.5, minVolume: 0.5f));
            _pipeline.AddDetector(new PitchDetector(refractorySeconds: 0.05));
            _pipeline.AddDetector(new AzimuthDetector(refractorySeconds: 0.05));
            _pipeline.AddDetector(new SpectralFeaturesDetector(refractorySeconds: 0.05));

            SwitchToLive();

            Position = 0;
            SliderPosition = 0;
            StatusText = $"{Path.GetFileName(path)} — {Duration:F1}s, {_player.SampleRate} Hz, {_player.Channels} ch";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            Duration = 0;
            CurrentFilePath = null;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void Play()
    {
        if (CurrentFilePath is null) return;

        // Если играем с начала, а мы в replay (после seek) — возвращаемся в live.
        if (!_isLiveAnalysis && _player.Position < 0.1)
            SwitchToLive();

        _player.Play();
        IsPlaying = true;
    }

    [RelayCommand]
    private void Pause()
    {
        _player.Pause();
        IsPlaying = false;
    }

    [RelayCommand]
    private void Stop()
    {
        StopPlayback();
        Position = 0;
        SliderPosition = 0;
        IsPlaying = false;
        _lastTickPosition = 0;
    }

    [RelayCommand]
    public void LaunchVisualizer()
    {
        var exePath = Settings.GodotExePath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            StatusText = "Visualizer: Godot executable path is not configured";
            return;
        }

        var projectPath = Settings.GodotProjectPath;
        if (string.IsNullOrEmpty(projectPath)
            || !Directory.Exists(projectPath)
            || !File.Exists(Path.Combine(projectPath, "project.godot")))
        {
            StatusText = "Visualizer: Godot project folder is not configured (need project.godot)";
            return;
        }

        try
        {
            _ipcServer.LaunchGodot(exePath, projectPath);
        }
        catch (Exception ex)
        {
            StatusText = $"Visualizer launch failed: {ex.Message}";
            return;
        }

        var proc = _ipcServer.GodotProcess;
        if (proc is null)
        {
            StatusText = "Visualizer: Process.Start returned no handle";
            return;
        }

        StatusText = $"Visualizer launched (PID {proc.Id})";

        // Проверяем через 750 мс: если Godot упал, показываем код выхода.
        var captured = proc;
        _ = Task.Run(async () =>
        {
            try
            {
                if (captured.WaitForExit(750))
                {
                    var code = captured.ExitCode;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        StatusText = $"Godot exited immediately (code {code}) — run under F5 to see stderr");
                }
            }
            catch { /* handle disposed */ }
        });
    }

    [RelayCommand]
    private async Task CopyDebugTableAsync()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Time(s)\tType\tConf\tParams\tSpectrum(32 bands)");
        foreach (var row in Events)
        {
            sb.AppendLine($"{row.Timestamp:F3}\t{row.Type}\t{row.Confidence:F2}\t{row.ParamsDisplay}\t{row.SpectrumString}");
        }

        var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (window?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(sb.ToString());
            StatusText = "Debug table copied to clipboard!";
        }
    }

    private void StopPlayback()
    {
        if (CurrentFilePath is not null) _player.Stop();
    }

    public void SeekTo(double seconds)
    {
        if (CurrentFilePath is null) return;
        _player.Seek(seconds);
        Position = seconds;
        SliderPosition = seconds;
        _pipeline?.Reset();
        SwitchToReplay();   // detach + сброс sink + флаг
    }

    public async Task SaveProjectAsync(string targetPath)
    {
        if (CurrentFilePath is null || _pipeline is null) return;

        try
        {
            StatusText = $"Computing SHA-256 for {Path.GetFileName(CurrentFilePath)}…";
            var sha = await ComputeSha256Async(CurrentFilePath, _lifetimeCts.Token);

            var timeline = new EventTimeline
            {
                // ... без изменений
            };

            var project = new ProjectDocument
            {
                // ... без изменений
            };

            ProjectSerializer.Save(project, targetPath);
            StatusText = $"Saved {Events.Count} events → {Path.GetFileName(targetPath)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Save cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    private static long ToTickMs(double seconds) => (long)Math.Round(seconds * 1000.0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _lifetimeCts.Cancel();          // ← для 6.15 ниже

        _pipeline?.Dispose();
        _player.Dispose();
        _clock.Dispose();
        _ipcServer.Dispose();
        BassBootstrap.Shutdown();       // ← последним: фри'ит глобальный BASS state

        _lifetimeCts.Dispose();
    }

    private void SwitchToLive()
    {
        if (_pipeline is null) return;
        _pipeline.Attach(_player);
        _pipeline.Sink.DrainAll();          // сброс стейл-событий, накопленных в replay
        _isLiveAnalysis = true;
        _lastTickPosition = _player.Position;
    }

    private void SwitchToReplay()
    {
        if (_pipeline is null) { _isLiveAnalysis = false; return; }
        _pipeline.Detach();                 // ← аудио-колбэк больше не кормит детекторы
        _pipeline.Sink.DrainAll();          // сброс накопленного в live до переключения
        _isLiveAnalysis = false;
        _lastTickPosition = _player.Position;
    }
}