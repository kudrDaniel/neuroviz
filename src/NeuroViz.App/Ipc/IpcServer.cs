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
    private volatile bool _eventStreamConnected;

    public void LaunchGodot(string godotExePath, string projectPath)
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

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");

        // Осушаем пайпы, иначе дочерний процесс может заблокироваться,
        // когда буфер заполнится. Godot пишет много в отладочном режиме.
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine($"[Godot]  {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine($"[Godot!] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _godotProcess = proc;

        _ = Task.Run(AcceptControlPipeAsync);
        _ = Task.Run(AcceptEventStreamAsync);
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

    private async Task AcceptControlPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    IpcConstants.ControlPipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);

                // Handshake: read Hello, send HelloAck.
                var helloBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (helloBytes == null) continue;
                var hello = MessagePackSerializer.Deserialize<ControlMessage>(
                    helloBytes, MessagePackOptions.Default);
                if (hello.Kind != ControlKinds.Hello) continue;

                var ack = new ControlMessage
                {
                    Kind = ControlKinds.HelloAck,
                    Payload = MessagePackSerializer.Serialize(
                        new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion },
                        MessagePackOptions.Default),
                };
                await PipeProtocol.WriteFrameAsync(
                    pipe, MessagePackSerializer.Serialize(ack, MessagePackOptions.Default), _cts.Token);

                // P2-B: keep pipe alive, drain control queue to it. Godot
                // reads these on its long-lived Control pipe loop.
                try
                {
                    while (pipe.IsConnected && !_cts.IsCancellationRequested)
                    {
                        if (_controlQueue.Reader.TryRead(out var msg))
                        {
                            await PipeProtocol.WriteFrameAsync(
                                pipe,
                                MessagePackSerializer.Serialize(msg, MessagePackOptions.Default),
                                _cts.Token);
                        }
                        else
                        {
                            await Task.Delay(50, _cts.Token);
                        }
                    }
                }
                catch { /* client closed pipe — loop re-accepts */ }
            }
            catch { /* accept/handshake failed — loop retries */ }
        }
    }

    private async Task AcceptEventStreamAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    IpcConstants.EventStreamPipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);

                // Сбрасываем всё, что накопилось до подключения (устаревшее).
                while (_outgoingFrames.Reader.TryRead(out _)) { }
                _eventStreamConnected = true;

                try
                {
                    while (pipe.IsConnected && !_cts.IsCancellationRequested)
                    {
                        if (_outgoingFrames.Reader.TryRead(out var frame))
                        {
                            var bytes = MessagePackSerializer.Serialize(frame, MessagePackOptions.Default);
                            await PipeProtocol.WriteFrameAsync(pipe, bytes, _cts.Token);
                        }
                        else
                        {
                            await Task.Delay(16, _cts.Token); // ~60 fps
                        }
                    }
                }
                finally
                {
                    _eventStreamConnected = false;
                }
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _outgoingFrames.Writer.TryComplete();
        _controlQueue.Writer.TryComplete();

        if (_godotProcess != null && !_godotProcess.HasExited)
        {
            try { _godotProcess.Kill(); } catch { /* already dead */ }
            _godotProcess.Dispose();
        }

        _cts.Dispose();
    }
}
