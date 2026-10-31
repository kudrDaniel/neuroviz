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

    private Process? _godotProcess;
    private long _seq = 0;
    private volatile bool _eventStreamConnected;

    public void LaunchGodot(string godotExePath, string projectPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = godotExePath,
            Arguments = $"--path \"{projectPath}\"",
            UseShellExecute = false
        };
        _godotProcess = Process.Start(psi);

        _ = Task.Run(AcceptControlPipeAsync);
        _ = Task.Run(AcceptEventStreamAsync);
    }

    public void PushFrame(double audioClockSeconds, IReadOnlyList<AudioEvent> events, Dictionary<string, ParamValue> paramValues)
    {
        // Визуализатор не подключён — фрейм бесполезен, не копим.
        if (!_eventStreamConnected) return;

        var frame = new EventStreamFrame
        {
            AudioClockSeconds = audioClockSeconds,
            Events = events.ToList(),
            Params = paramValues,
            FrameSeq = Interlocked.Increment(ref _seq)
        };

        // Bounded + DropOldest: при переполнении вытесняется самый старый кадр.
        _outgoingFrames.Writer.TryWrite(frame);
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

                var bytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (bytes != null)
                {
                    var msg = MessagePackSerializer.Deserialize<ControlMessage>(bytes, MessagePackOptions.Default);
                    if (msg.Kind == ControlKinds.Hello)
                    {
                        var ack = new ControlMessage
                        {
                            Kind = ControlKinds.HelloAck,
                            Payload = MessagePackSerializer.Serialize(
                                new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion },
                                MessagePackOptions.Default)
                        };
                        await PipeProtocol.WriteFrameAsync(
                            pipe, MessagePackSerializer.Serialize(ack, MessagePackOptions.Default), _cts.Token);
                    }
                }
            }
            catch { /* ignore */ }
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

        if (_godotProcess != null && !_godotProcess.HasExited)
        {
            try { _godotProcess.Kill(); } catch { /* already dead */ }
            _godotProcess.Dispose();
        }

        _cts.Dispose();
    }
}