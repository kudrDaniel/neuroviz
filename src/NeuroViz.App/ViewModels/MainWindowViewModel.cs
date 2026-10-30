using System.Collections.ObjectModel;
using System.Security.Cryptography;
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
    private AnalyzerPipeline? _pipeline;
    private bool _disposed;

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

    public MainWindowViewModel()
    {
        _clock = new AudioClockWriter();
        _player = new ManagedBassAudioPlayer(_clock);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _ipcServer = new IpcServer();
        // Загружаем правила из samples/rules/basic.json (или создаем дефолтное)
        var rulesPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../samples/rules/basic.json"));
        var rulesJson = File.Exists(rulesPath)
            ? File.ReadAllText(rulesPath)
            : "{ \"version\": 1, \"rules\": [] }";
        _ruleEngine = new RuleEngine(RulesLoader.FromJson(rulesJson));
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsDragging)
        {
            Position = _player.Position;
            SliderPosition = Position;
        }
        IsPlaying = _player.IsPlaying;
        DetectorErrors = _pipeline?.DetectorErrors ?? 0;

        if (_pipeline is not null)
        {
            var batch = _pipeline.Sink.DrainAll();
            var paramValues = new Dictionary<string, ParamValue>();

            foreach (var ev in batch)
            {
                Events.Add(EventRow.From(ev));

                // Оцениваем правила
                foreach (var action in _ruleEngine.Process(ev, ev.TimestampSeconds))
                {
                    paramValues[action.Action.Param] = new ParamValue
                    {
                        Value = action.Action.Value,
                        SetAtSeconds = action.AtSeconds,
                        DecayMs = action.Action.DecayMs
                    };
                }
            }

            if (CurrentFilePath != null)
            {
                // Отправляем кадр в Godot (даже если batch пустой, чтобы слать audio clock)
                _ipcServer.PushFrame(_player.Position, batch, paramValues);
            }
        }
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
            _pipeline.AddDetector(new OnsetDetector());
            _pipeline.Attach(_player);

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
                    ["channels"] = _player.Channels.ToString(),
                    ["detectors"] = "onset",
                },
            };

            var project = new ProjectDocument
            {
                Name = Path.GetFileNameWithoutExtension(CurrentFilePath),
                CreatedAt = DateTimeOffset.UtcNow,
                Timeline = timeline,
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
        _pipeline?.Dispose();
        _player.Dispose();
        _clock.Dispose();
    }
}