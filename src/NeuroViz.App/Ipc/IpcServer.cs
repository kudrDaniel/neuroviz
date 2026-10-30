using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using NeuroViz.Core.Events;
using NeuroViz.Core.Ipc;
using NeuroViz.Core.Serialization;

namespace NeuroViz.App.Ipc;

public sealed class IpcServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<EventStreamFrame> _outgoingFrames = new();
    private Process? _godotProcess;
    private long _seq = 0;

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
        _outgoingFrames.Enqueue(new EventStreamFrame
        {
            AudioClockSeconds = audioClockSeconds,
            Events = events.ToList(),
            Params = paramValues,
            FrameSeq = Interlocked.Increment(ref _seq)
        });
    }

    private async Task AcceptControlPipeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(IpcConstants.ControlPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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
                            Payload = MessagePackSerializer.Serialize(new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion }, MessagePackOptions.Default)
                        };
                        await PipeProtocol.WriteFrameAsync(pipe, MessagePackSerializer.Serialize(ack, MessagePackOptions.Default), _cts.Token);
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
                using var pipe = new NamedPipeServerStream(IpcConstants.EventStreamPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);

                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    if (_outgoingFrames.TryDequeue(out var frame))
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
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_godotProcess != null && !_godotProcess.HasExited)
        {
            _godotProcess.Kill();
            _godotProcess.Dispose();
        }
    }
}