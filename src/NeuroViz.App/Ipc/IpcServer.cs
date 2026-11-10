using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MessagePack;
using NeuroViz.Core.Events;
using NeuroViz.Core.Ipc;
using NeuroViz.Core.Serialization;

namespace NeuroViz.App.Ipc;

public sealed class IpcServer : IDisposable
{
    private const int EventStreamBufferCapacity = 8; // ~260 мс при 30 fps

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<EventStreamFrame> _outgoingFrames =
        Channel.CreateBounded<EventStreamFrame>(new BoundedChannelOptions(EventStreamBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Channel<ControlMessage> _controlQueue =
    Channel.CreateBounded<ControlMessage>(new BoundedChannelOptions(16)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private Process? _godotProcess;
    public Process? GodotProcess => _godotProcess;
    private long _seq = 0;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private volatile bool _eventStreamConnected;

    /// <summary>
    /// Fires on the thread that drains the Preview pipe (background). Callers
    /// are responsible for marshalling to the UI thread (Dispatcher.UIThread.Post).
    /// </summary>
    public event Action<PreviewFrame>? PreviewReceived;

    private Task? _controlTask;
    private Task? _eventTask;
    private Task? _previewTask;

    public IpcServer()
    {
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        IpcConstants.ControlPipeName = $"NeuroViz.Control.{pid}";
        IpcConstants.EventStreamPipeName = $"NeuroViz.EventStream.{pid}";
        IpcConstants.PreviewPipeName = $"NeuroViz.Preview.{pid}";
        IpcConstants.AudioClockMapName = $"NeuroViz.AudioClock.{pid}";
    }

    public void LaunchGodot(string godotExePath, string projectPath, SetVisualizerScenePayload config)
    {
        // Defense-in-depth: trailing separator перед закрывающей кавычкой
        // ломает парсер аргументов Windows. Убираем его до любых манипуляций.
        projectPath = projectPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!File.Exists(godotExePath))
            throw new FileNotFoundException("Godot executable not found", godotExePath);
        var projectFile = Path.Combine(projectPath, "project.godot");
        if (!File.Exists(projectFile))
            throw new FileNotFoundException("project.godot not found", projectFile);

        // ArgumentList: .NET применяет канонические правила квотирования
        // Windows — включая случай trailing-backslash-before-quote. Ручная
        // склейка $"--path \"{path}\"" на этом и погорела.
        var psi = new ProcessStartInfo
        {
            FileName = godotExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(projectPath);
        AppendSceneArgs(psi, config);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");

        // Осушаем пайпы, иначе дочерний процесс может заблокироваться,
        // когда буфер заполнится. Godot пишет много в отладочном режиме.
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine($"[Godot]  {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine($"[Godot!] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _godotProcess = proc;

        _controlTask = Task.Run(AcceptControlPipeAsync);
        _eventTask = Task.Run(AcceptEventStreamAsync);
        _previewTask = Task.Run(AcceptPreviewAsync);
    }

    private static void AppendSceneArgs(ProcessStartInfo psi, SetVisualizerScenePayload config)
    {
        // Godot strips everything before "--" and exposes the rest via
        // OS.GetCmdlineUserArgs().
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add($"--nv-ipc-suffix={System.Diagnostics.Process.GetCurrentProcess().Id}");
        psi.ArgumentList.Add($"--nv-scene={config.Scene}");
        psi.ArgumentList.Add($"--nv-nodes={config.NeuralNodesPerHemisphere}");
        psi.ArgumentList.Add($"--nv-edges={config.NeuralEdgesPerNode}");
        psi.ArgumentList.Add($"--nv-cc={config.NeuralCCDensity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    public void PushFrame(
    double audioClockSeconds,
    IReadOnlyList<AudioEvent> events,
    Dictionary<string, ParamValue> paramValues,
    IReadOnlyDictionary<string, float> continuousParams)
    {
        if (!_eventStreamConnected) return;

        var frame = new EventStreamFrame
        {
            AudioClockSeconds = audioClockSeconds,
            Events = events.ToList(),
            Params = paramValues,
            FrameSeq = Interlocked.Increment(ref _seq),
            // Clone: caller's dict is reused across ticks in replay (empty static).
            ContinuousParams = continuousParams.Count > 0
                ? new Dictionary<string, float>(continuousParams)
                : new(),
        };

        _outgoingFrames.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Queues a SetWindowMode command. Delivered on the long-lived Control
    /// pipe when a Visualizer is connected; buffered if not (next launch
    /// receives it). Idempotent, non-blocking.
    /// </summary>
    public void SendWindowMode(int monitorIndex, bool fullscreen)
    {
        var msg = new ControlMessage
        {
            Kind = ControlKinds.SetWindowMode,
            Payload = MessagePackSerializer.Serialize(
                new SetWindowModePayload { MonitorIndex = monitorIndex, Fullscreen = fullscreen },
                MessagePackOptions.Default),
        };
        _controlQueue.Writer.TryWrite(msg);
    }

    public void SendAlwaysOnTop(bool enabled)
    {
        var msg = new ControlMessage
        {
            Kind = ControlKinds.SetAlwaysOnTop,
            Payload = MessagePackSerializer.Serialize(
                new SetAlwaysOnTopPayload { Enabled = enabled },
                MessagePackOptions.Default),
        };
        _controlQueue.Writer.TryWrite(msg);
    }

    public void SendVisualConfig(SetVisualConfigPayload payload)
    {
        var msg = new ControlMessage
        {
            Kind = ControlKinds.SetVisualConfig,
            Payload = MessagePackSerializer.Serialize(payload, MessagePackOptions.Default),
        };
        _controlQueue.Writer.TryWrite(msg);
    }

    public void SendVisualizerScene(SetVisualizerScenePayload payload)
    {
        var msg = new ControlMessage
        {
            Kind = ControlKinds.SetVisualizerScene,
            Payload = MessagePackSerializer.Serialize(payload, MessagePackOptions.Default),
        };
        _controlQueue.Writer.TryWrite(msg);
    }

    private async Task AcceptControlPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    IpcConstants.ControlPipeName, PipeDirection.InOut, 5, // 5 инстансов дают ОС время на очистку зомби-сокетов
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);

                // Отдаем клиента в изолированный Task и мгновенно возвращаемся в цикл!
                _ = Task.Run(() => HandleControlClientAsync(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(100, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task HandleControlClientAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                var helloBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (helloBytes == null) return;

                var hello = MessagePackSerializer.Deserialize<ControlMessage>(helloBytes, MessagePackOptions.Default);
                if (hello.Kind != ControlKinds.Hello) return;

                var ack = new ControlMessage
                {
                    Kind = ControlKinds.HelloAck,
                    Payload = MessagePackSerializer.Serialize(
                        new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion },
                        MessagePackOptions.Default),
                };
                await PipeProtocol.WriteFrameAsync(pipe, MessagePackSerializer.Serialize(ack, MessagePackOptions.Default), _cts.Token);

                var lastWriteTime = _sw.Elapsed.TotalSeconds;

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    if (_controlQueue.Reader.TryRead(out var msg))
                    {
                        await PipeProtocol.WriteFrameAsync(
                            pipe,
                            MessagePackSerializer.Serialize(msg, MessagePackOptions.Default),
                            _cts.Token);
                        lastWriteTime = _sw.Elapsed.TotalSeconds;
                    }
                    else
                    {
                        var elapsed = _sw.Elapsed.TotalSeconds;
                        if (elapsed - lastWriteTime > 0.5)
                        {
                            var heartbeat = MessagePackSerializer.Serialize(
                                new ControlMessage { Kind = "" },
                                MessagePackOptions.Default);
                            await PipeProtocol.WriteFrameAsync(pipe, heartbeat, _cts.Token);
                            lastWriteTime = elapsed;
                        }
                        await Task.Delay(50, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex) { Debug.WriteLine($"[APP:CONTROL] Client Error: {ex.Message}"); }
        }
    }

    private async Task AcceptEventStreamAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    IpcConstants.EventStreamPipeName, PipeDirection.Out, 5,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);

                _ = Task.Run(() => HandleEventStreamClientAsync(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(100, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task HandleEventStreamClientAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                _eventStreamConnected = true;
                while (_outgoingFrames.Reader.TryRead(out _)) { }

                var lastWriteTime = _sw.Elapsed.TotalSeconds;

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    if (_outgoingFrames.Reader.TryRead(out var frame))
                    {
                        var bytes = MessagePackSerializer.Serialize(frame, MessagePackOptions.Default);
                        await PipeProtocol.WriteFrameAsync(pipe, bytes, _cts.Token);
                        lastWriteTime = _sw.Elapsed.TotalSeconds;
                    }
                    else
                    {
                        var elapsed = _sw.Elapsed.TotalSeconds;
                        if (elapsed - lastWriteTime > 0.5)
                        {
                            var heartbeat = MessagePackSerializer.Serialize(
                                new EventStreamFrame { FrameSeq = -1 },
                                MessagePackOptions.Default);
                            await PipeProtocol.WriteFrameAsync(pipe, heartbeat, _cts.Token);
                            lastWriteTime = elapsed;
                        }
                        await Task.Delay(16, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex) { Debug.WriteLine($"[APP:EVENT] Client Error: {ex.Message}"); }
            finally
            {
                _eventStreamConnected = false;
            }
        }
    }

    private async Task AcceptPreviewAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    IpcConstants.PreviewPipeName, PipeDirection.In, 5,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);

                _ = Task.Run(() => HandlePreviewClientAsync(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(100, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task HandlePreviewClientAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var bytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                    if (bytes == null) break;

                    var frame = MessagePackSerializer.Deserialize<PreviewFrame>(bytes, MessagePackOptions.Default);
                    PreviewReceived?.Invoke(frame);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex) { Debug.WriteLine($"[APP:PREVIEW] Client Error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Ждем, пока фоновые задачи завершатся и освободят пайпы
        var tasks = new[] { _controlTask, _eventTask, _previewTask }
            .Where(t => t != null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { Task.WaitAll(tasks, 1000); } catch { }
        }

        _outgoingFrames.Writer.TryComplete();
        _controlQueue.Writer.TryComplete();

        if (_godotProcess != null && !_godotProcess.HasExited)
        {
            try { _godotProcess.Kill(); } catch { }
            _godotProcess.Dispose();
        }
        _cts.Dispose();
    }

    private static void Log(string tag, string msg)
    {
        Debug.WriteLine($"[{DateTime.Now:mm:ss.fff}] [APP:{tag}] {msg}");
    }
}
