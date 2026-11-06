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
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfView;   // доступ только через Volatile.Read/Write
    private long _lastSeq = -1;

    public bool IsConnected { get; private set; }

    public void Start()
    {
        _ = Task.Run(ConnectControlPipeAsync);
        _ = Task.Run(ListenEventStreamAsync);
        _ = Task.Run(OpenAudioClockAsync);
        _ = Task.Run(ServePreviewPipeAsync);
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
                using var pipe = new NamedPipeClientStream(
                    ".", IpcConstants.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, _cts.Token);

                var hello = new ControlMessage
                {
                    Kind = ControlKinds.Hello,
                    Payload = MessagePackSerializer.Serialize(
                        new HelloPayload
                        {
                            ProtocolVersion = IpcConstants.ProtocolVersion,
                            Role = "visualizer",
                        },
                        MessagePackOptions.Default),
                };
                await PipeProtocol.WriteFrameAsync(
                    pipe, MessagePackSerializer.Serialize(hello, MessagePackOptions.Default), _cts.Token);

                var ackBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (ackBytes == null) continue; // server closed before ack
                var ack = MessagePackSerializer.Deserialize<ControlMessage>(
                    ackBytes, MessagePackOptions.Default);
                if (ack.Kind != ControlKinds.HelloAck) continue;

                IsConnected = true;
                GD.Print("[IPC] Control pipe handshake OK, entering read loop.");

                // P2-B: long-lived read loop. Server sends SetWindowMode /
                // SetAlwaysOnTop and any future command kinds here.
                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var cmdBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                    if (cmdBytes == null) break;   // server closed pipe
                    var cmd = MessagePackSerializer.Deserialize<ControlMessage>(
                        cmdBytes, MessagePackOptions.Default);
                    HandleControlMessage(cmd);
                }

                IsConnected = false;
                GD.Print("[IPC] Control pipe closed, will reconnect.");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                GD.PrintErr($"[IPC] Control pipe error: {ex.Message}");
                await Task.Delay(1000, _cts.Token);
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
                using var pipe = new NamedPipeClientStream(".", IpcConstants.EventStreamPipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, _cts.Token);
                GD.Print("[IPC] EventStream connected.");

                while (!_cts.IsCancellationRequested)
                {
                    var bytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                    if (bytes == null) break; // Сервер закрыл пайп
                    var frame = MessagePackSerializer.Deserialize<EventStreamFrame>(bytes, MessagePackOptions.Default);
                    _frameQueue.Enqueue(frame);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[IPC] EventStream error: {ex.Message}");
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    private async Task ServePreviewPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    IpcConstants.PreviewPipeName,
                    PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);
                GD.Print("[IPC] Preview pipe connected.");
                _previewConnected = true;

                // Drop any stale frames buffered while disconnected.
                while (_previewQueue.Reader.TryRead(out _)) { }

                try
                {
                    while (pipe.IsConnected && !_cts.IsCancellationRequested)
                    {
                        if (_previewQueue.Reader.TryRead(out var frame))
                        {
                            var bytes = MessagePackSerializer.Serialize(
                                frame, MessagePackOptions.Default);
                            await PipeProtocol.WriteFrameAsync(pipe, bytes, _cts.Token);
                        }
                        else
                        {
                            await Task.Delay(20, _cts.Token);   // 50 Hz drain
                        }
                    }
                }
                finally
                {
                    _previewConnected = false;
                }
                GD.Print("[IPC] Preview pipe closed, waiting for reconnect.");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _previewConnected = false;
                GD.PrintErr($"[IPC] Preview pipe error: {ex.Message}");
                try { await Task.Delay(1000, _cts.Token); } catch { }
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
        _cts.Cancel();
        Volatile.Read(ref _mmfView)?.Dispose();
        _mmf?.Dispose();
        _cts.Dispose();
    }
}