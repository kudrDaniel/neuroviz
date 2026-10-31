using System;
using System.Collections.Concurrent;
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
    private readonly CancellationTokenSource _cts = new();
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfView;   // доступ только через Volatile.Read/Write
    private long _lastSeq = -1;

    public bool IsConnected { get; private set; }

    public void Start()
    {
        _ = Task.Run(ConnectControlPipeAsync);
        _ = Task.Run(ListenEventStreamAsync);
        _ = Task.Run(OpenAudioClockAsync);   // было OpenAudioClock
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
                using var pipe = new NamedPipeClientStream(".", IpcConstants.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, _cts.Token);
                var hello = new ControlMessage
                {
                    Kind = ControlKinds.Hello,
                    Payload = MessagePackSerializer.Serialize(new HelloPayload { ProtocolVersion = IpcConstants.ProtocolVersion, Role = "visualizer" }, MessagePackOptions.Default)
                };
                await PipeProtocol.WriteFrameAsync(pipe, MessagePackSerializer.Serialize(hello, MessagePackOptions.Default), _cts.Token);

                // Ждем HelloAck
                var ackBytes = await PipeProtocol.ReadFrameAsync(pipe, _cts.Token);
                if (ackBytes != null)
                {
                    var ack = MessagePackSerializer.Deserialize<ControlMessage>(ackBytes, MessagePackOptions.Default);
                    if (ack.Kind == ControlKinds.HelloAck)
                    {
                        IsConnected = true;
                        GD.Print("[IPC] Control pipe connected & handshake OK.");
                        // После handshake Control-пайп можно закрыть (если не нужны другие команды)
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[IPC] Control pipe error: {ex.Message}");
                await Task.Delay(1000, _cts.Token);
            }
        }
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