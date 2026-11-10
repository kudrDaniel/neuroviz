using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using NeuroViz.Core.Ipc;
using NeuroViz.Core.Serialization;
using Godot;

namespace NeuroViz.Visualizer.Scripts;

public sealed class IpcClient : IDisposable
{
    private readonly ConcurrentQueue<EventStreamFrame> _frameQueue = new();
    private readonly ConcurrentQueue<Action> _pendingMainThreadActions = new();
    private readonly Channel<PreviewFrame> _previewQueue =
    Channel.CreateBounded<PreviewFrame>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,   // main thread only
    });
    private long _previewSeq;
    private double _lastPreviewCaptureSeconds = -1.0;
    private volatile bool _previewConnected;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _controlPipe;
    private NamedPipeClientStream? _eventPipe;
    private NamedPipeClientStream? _previewPipe;
    private Task? _controlTask;
    private Task? _eventTask;
    private Task? _clockTask;
    private Task? _previewTask;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfView;   // доступ только через Volatile.Read/Write
    private long _lastSeq = -1;

    public bool IsConnected { get; private set; }

    /// <summary>
    /// Fires on the main thread (via the pending-action queue) when the
    /// App pushes a SetVisualConfig command. Subscribers apply the values
    /// to live objects (camera, particle emitters).
    /// </summary>
    public event Action<SetVisualConfigPayload>? VisualConfigReceived;

    /// <summary>
    /// Path to the scene a SetVisualizerScene command asked us to switch to.
    /// Set by HandleControlMessage (background thread), consumed and cleared
    /// by the active scene's _Process (main thread, safe place for ChangeSceneToFile).
    /// </summary>
    public string? PendingScenePath { get; private set; }

    public void ClearPendingScene() => PendingScenePath = null;

    public void Start()
    {
        _controlTask = Task.Run(ConnectControlPipeAsync);
        _eventTask = Task.Run(ListenEventStreamAsync);
        _clockTask = Task.Run(OpenAudioClockAsync);
        _previewTask = Task.Run(ServePreviewPipeAsync);
    }

    /// <summary>
    /// Свежее время воспроизведения из разделяемой памяти (обновляется
    /// на каждый DSP-колбэк, ~10 мс). null — MMF ещё не открыт.
    /// </summary>
    public double? GetAudioClockSeconds()
    {
        var view = Volatile.Read(ref _mmfView);
        if (view == null) return null;
        try
        {
            var ticks = view.ReadInt64(AudioClockLayout.OffsetAudioPosTicksNs);
            return ticks / 1_000_000_000.0;
        }
        catch { return null; }
    }

    /// <summary>
    /// True when the App-side inspector is connected to the preview pipe.
    /// MainNode/_Process should only capture when this is true — capture is
    /// a GPU→CPU round-trip (expensive at viewport resolution) and is pure
    /// waste if nobody is listening.
    /// </summary>
    public bool IsPreviewConnected => _previewConnected;

    /// <summary>
    /// Rate-limits capture to 5 fps. Called on main thread once per _Process.
    /// Returns true → caller should do the viewport capture and call
    /// <see cref="QueuePreview"/>.
    /// </summary>
    public bool ShouldCapturePreview(double nowSeconds)
    {
        if (!_previewConnected) return false;
        if (nowSeconds - _lastPreviewCaptureSeconds < 0.2) return false;
        _lastPreviewCaptureSeconds = nowSeconds;
        return true;
    }

    /// <summary>
    /// Enqueues a captured and resized image for the preview writer. Resizing
    /// must be done on the main thread (Image API); only the PNG encode and
    /// pipe write happen off-thread.
    /// </summary>
    public void QueuePreview(Image image)
    {
        var png = image.SavePngToBuffer();
        var frame = new PreviewFrame
        {
            FrameSeq = Interlocked.Increment(ref _previewSeq),
            Width = image.GetWidth(),
            Height = image.GetHeight(),
            PngBytes = png,
        };
        _previewQueue.Writer.TryWrite(frame);
    }

    public EventStreamFrame? GetLatestFrame()
    {
        EventStreamFrame? latest = null;
        while (_frameQueue.TryDequeue(out var frame))
        {
            if (frame.FrameSeq > _lastSeq)
            {
                _lastSeq = frame.FrameSeq;
                latest = frame;
            }
        }
        return latest;
    }

    private async Task ConnectControlPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Log("CONTROL", "Connecting to server...");
                _controlPipe = new NamedPipeClientStream(".", IpcConstants.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var pipe = _controlPipe;
                await pipe.ConnectAsync(2000, _cts.Token);
                Log("CONTROL", "CONNECTED.");

                var hello = new ControlMessage { Kind = ControlKinds.Hello, Payload = MessagePackSerializer.Serialize(new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion, Role = "visualizer" }, MessagePackOptions.Default) };
                await PipeProtocol.WriteFrameAsync(pipe, MessagePackSerializer.Serialize(hello, MessagePackOptions.Default), _cts.Token);

                var ackBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (ackBytes == null) { Log("CONTROL", "Server disconnected before ack."); continue; }

                IsConnected = true;
                Log("CONTROL", "Handshake OK. Entering read loop.");

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var cmdBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                    if (cmdBytes == null) break;
                    var cmd = MessagePackSerializer.Deserialize<ControlMessage>(cmdBytes, MessagePackOptions.Default);
                    HandleControlMessage(cmd);
                }
                IsConnected = false;
                Log("CONTROL", "Connection closed.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                IsConnected = false;
                Log("CONTROL", $"ERROR: {ex.Message}");
                try { await Task.Delay(500, _cts.Token); } catch { break; }
            }
        }
    }

    private void HandleControlMessage(ControlMessage msg)
    {
        // DisplayServer.* is main-thread-only in Godot 4. We're on a background
        // thread here (Task.Run of ConnectControlPipeAsync) — calling it directly
        // deadlocks the display server and freezes the whole Godot process
        // (window stops updating, cannot be closed, cannot be focused).
        // Enqueue an action; the scene drains it in _Process (main thread).
        switch (msg.Kind)
        {
            case ControlKinds.SetWindowMode:
                var mode = MessagePackSerializer.Deserialize<SetWindowModePayload>(
                    msg.Payload, MessagePackOptions.Default);
                _pendingMainThreadActions.Enqueue(
                    () => ApplyWindowMode(mode.MonitorIndex, mode.Fullscreen));
                break;
            case ControlKinds.SetAlwaysOnTop:
                var top = MessagePackSerializer.Deserialize<SetAlwaysOnTopPayload>(
                    msg.Payload, MessagePackOptions.Default);
                _pendingMainThreadActions.Enqueue(
                    () => ApplyAlwaysOnTop(top.Enabled));
                break;
            case ControlKinds.SetVisualConfig:
                var visual = MessagePackSerializer.Deserialize<SetVisualConfigPayload>(
                    msg.Payload, MessagePackOptions.Default);
                NeuralSceneConfig.CameraOrbitSpeed = visual.NeuralCameraOrbitSpeed;
                NeuralSceneConfig.CameraYAmplitude = visual.NeuralCameraYAmplitude;
                _pendingMainThreadActions.Enqueue(() => VisualConfigReceived?.Invoke(visual));
                break;
            case ControlKinds.SetVisualizerScene:
                var sceneMsg = MessagePackSerializer.Deserialize<SetVisualizerScenePayload>(
                    msg.Payload, MessagePackOptions.Default);
                // Populate static topology config BEFORE scene reload. The new
                // scene's _Ready runs after ChangeSceneToFile, in the same main-
                // thread frame or the next.
                NeuralSceneConfig.NodesPerHemisphere = sceneMsg.NeuralNodesPerHemisphere;
                NeuralSceneConfig.EdgesPerNode = sceneMsg.NeuralEdgesPerNode;
                NeuralSceneConfig.CCDensity = sceneMsg.NeuralCCDensity;
                var path = sceneMsg.Scene switch
                {
                    "2d" => "res://node_2d.tscn",
                    "neural" => "res://neural.tscn",
                    _ => "res://spike.tscn",
                };
                // We can't touch SceneTree here (background thread). Set the
                // pending path; each scene's _Process will pick it up on main thread.
                PendingScenePath = path;
                break;
            case "":
                // Heartbeat from App (N.9-fix2). Ignore silently — not an
                // unknown kind, not an error.
                break;
            default:
                GD.Print($"[IPC] Unknown control kind: {msg.Kind}");
                break;
        }
    }

    /// <summary>
    /// Drains the main-thread action queue. Call from _Process (main thread)
    /// once per frame. Any Godot API called from HandleControlMessage must
    /// pass through here — see comment in HandleControlMessage.
    /// </summary>
    public void DrainMainThreadActions()
    {
        while (_pendingMainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                GD.PrintErr($"[IPC] Deferred control action failed: {ex.Message}");
            }
        }
    }

    private static void ApplyWindowMode(int monitorIndex, bool fullscreen)
    {
        var screenCount = DisplayServer.GetScreenCount();
        if (monitorIndex < 0 || monitorIndex >= screenCount) return;

        // Transition through windowed so SetCurrentScreen has an effect when
        // the window is already fullscreen on a different monitor.
        var currentMode = DisplayServer.WindowGetMode();
        if (currentMode == DisplayServer.WindowMode.Fullscreen
            || currentMode == DisplayServer.WindowMode.ExclusiveFullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }

        DisplayServer.WindowSetCurrentScreen(monitorIndex);

        if (fullscreen)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
    }

    private static void ApplyAlwaysOnTop(bool enabled)
    {
        DisplayServer.WindowSetFlag(
            DisplayServer.WindowFlags.AlwaysOnTop, enabled);
    }

    private async Task ListenEventStreamAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Log("EVENT", "Connecting to server...");
                _eventPipe = new NamedPipeClientStream(".", IpcConstants.EventStreamPipeName, PipeDirection.In, PipeOptions.Asynchronous);
                using var pipe = _eventPipe;
                await pipe.ConnectAsync(2000, _cts.Token);
                Log("EVENT", "CONNECTED.");

                while (!_cts.IsCancellationRequested)
                {
                    var bytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                    if (bytes == null) break;
                    var frame = MessagePackSerializer.Deserialize<EventStreamFrame>(bytes, MessagePackOptions.Default);
                    if (frame.FrameSeq > 0) _frameQueue.Enqueue(frame);
                }
                Log("EVENT", "Connection closed.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("EVENT", $"ERROR: {ex.Message}");
                try { await Task.Delay(500, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task ServePreviewPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Log("PREVIEW", "Connecting to server...");
                _previewPipe = new NamedPipeClientStream(".", IpcConstants.PreviewPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var pipe = _previewPipe;
                await pipe.ConnectAsync(2000, _cts.Token);
                Log("PREVIEW", "CONNECTED.");
                _previewConnected = true;

                while (_previewQueue.Reader.TryRead(out _)) { }

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    if (_previewQueue.Reader.TryRead(out var frame))
                    {
                        await PipeProtocol.WriteFrameAsync(pipe, MessagePackSerializer.Serialize(frame, MessagePackOptions.Default), _cts.Token);
                    }
                    else
                    {
                        await Task.Delay(20, _cts.Token);
                    }
                }
                _previewConnected = false;
                Log("PREVIEW", "Connection closed.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _previewConnected = false;
                Log("PREVIEW", $"ERROR: {ex.Message}");
                try { await Task.Delay(500, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task OpenAudioClockAsync()
    {
        while (!_cts.IsCancellationRequested && Volatile.Read(ref _mmfView) == null)
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(IpcConstants.AudioClockMapName);
                var view = _mmf.CreateViewAccessor(
                    0, AudioClockLayout.Size, MemoryMappedFileAccess.Read);
                Volatile.Write(ref _mmfView, view);
                GD.Print("[IPC] AudioClock mmap opened.");
                return;
            }
            catch (FileNotFoundException)
            {
                // Инспектор ещё не создал MMF — попробуем через секунду.
                await Task.Delay(1000, _cts.Token);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[IPC] mmap open failed: {ex.Message}");
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();

        // ПРИНУДИТЕЛЬНО закрываем сокеты. Это мгновенно прервет любые
        // висящие ReadAsync/WriteAsync и освободит хэндлы ОС.
        try { _controlPipe?.Dispose(); } catch { }
        try { _eventPipe?.Dispose(); } catch { }
        try { _previewPipe?.Dispose(); } catch { }

        var tasks = new[] { _controlTask, _eventTask, _clockTask, _previewTask }
            .Where(t => t != null).Cast<Task>().ToArray();

        if (tasks.Length > 0)
        {
            try { Task.WaitAll(tasks, 500); } catch { }
        }

        Volatile.Read(ref _mmfView)?.Dispose();
        _mmf?.Dispose();
        _cts.Dispose();
    }

    private void Log(string tag, string msg)
    {
        GD.Print($"[{Time.GetTicksMsec()}] [GODOT:{tag}] {msg}");
    }
}