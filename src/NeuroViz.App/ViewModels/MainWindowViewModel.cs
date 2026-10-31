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

    public MainWindowViewModel()
    {
        _clock = new AudioClockWriter();
        _player = new ManagedBassAudioPlayer(_clock);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _ipcServer = new IpcServer();

        // Загружаем правила из samples/rules/basic.json (или создаем дефолтное)
        var rulesPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "../../../../../samples/rules/basic.json"));
        var rulesJson = File.Exists(rulesPath)
            ? File.ReadAllText(rulesPath)
            : "{ \"version\": 1, \"rules\": [] }";

        _rulesDocument = RulesLoader.FromJson(rulesJson);   // ← сохраняем снапшот
        _ruleEngine = new RuleEngine(_rulesDocument);
    }

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
        List<AudioEvent> eventsToSend = new();
        double currentPos = _player.Position;

        if (_isLiveAnalysis && _pipeline != null)
        {
            // LIVE РЕЖИМ: берем точное время из анализатора и забираем ВСЕ новые события
            currentPos = _pipeline.LastAnalyzedTimestamp;
            var batch = _pipeline.Sink.DrainAll();

            foreach (var analyzed in batch)
            {
                // Добавляем в UI
                if (!Events.Any(r => r.Type == analyzed.Event.Type && Math.Abs(r.Timestamp - analyzed.Event.TimestampSeconds) < 0.001))
                {
                    Events.Add(EventRow.From(analyzed));
                }
                eventsToSend.Add(analyzed.Event);
            }
        }
        else
        {
            // REPLAY РЕЖИМ: ищем события в памяти за прошедший тик (с запасом 50 мс назад)
            var windowStart = _lastTickPosition - 0.05;
            eventsToSend = Events
                .Where(r => r.Timestamp > windowStart && r.Timestamp <= currentPos)
                .Select(r => r.Source)
                .ToList();
        }

        // Применяем правила к найденным событиям
        foreach (var ev in eventsToSend)
        {
            foreach (var action in _ruleEngine.Process(ev, ev.TimestampSeconds))
            {
                // Теперь action.Action больше нет, все свойства лежат прямо в action
                paramValues[action.Param] = new ParamValue
                {
                    Value = action.Value,
                    SetAtSeconds = action.AtSeconds,
                    DecayMs = action.DecayMs
                };
            }
        }

        // Отправляем в Godot (даже если пусто, чтобы обновить часы)
        _ipcServer.PushFrame(currentPos, eventsToSend, paramValues);
        _lastTickPosition = currentPos;
    }

    public async Task LoadFileAsync(string path)
    {
        try
        {
            StopPlayback();
            Events.Clear();
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
    private void LaunchVisualizer()
    {
        // ВАЖНО: Укажите ваш путь к исполняемому файлу Godot!
        var godotExe = @"E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe";
        var projectPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../src/NeuroViz.Visualizer"));

        _ipcServer.LaunchGodot(godotExe, projectPath);
        StatusText = "Visualizer launched";
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
            var sha = await ComputeSha256Async(CurrentFilePath);

            var timeline = new EventTimeline
            {
                AudioPath = Path.GetFullPath(CurrentFilePath),
                AudioSha256 = sha,
                DurationSeconds = Duration,
                Events = Events.Select(r => r.Source).ToList(),
                AnalysisMeta =
            {
                ["sampleRate"] = _player.SampleRate.ToString(),
                ["channels"]   = _player.Channels.ToString(),
                // Перечисляем то, что LoadFileAsync реально добавляет в pipeline.
                // При изменении набора детекторов — править ЗДЕСЬ и в LoadFileAsync.
                ["detectors"]  = "onset,novelty,pitch,azimuth,spectral_features",
            },
            };

            var project = new ProjectDocument
            {
                Name = Path.GetFileNameWithoutExtension(CurrentFilePath),
                CreatedAt = DateTimeOffset.UtcNow,
                Timeline = timeline,
                Rules = _rulesDocument,   // ← фикс: правила попадают в .nvproj
            };

            ProjectSerializer.Save(project, targetPath);
            StatusText = $"Saved {Events.Count} events → {Path.GetFileName(targetPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();

        _pipeline?.Dispose();     // отписывает OnPcm от плеера
        _player.Dispose();        // останавливает Bass + DSP-колбэк
        _clock.Dispose();         // безопасно: писателей больше нет
        _ipcServer.Dispose();     // kill Godot, TryComplete канала, _cts.Dispose
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