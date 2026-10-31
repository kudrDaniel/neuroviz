using System.Threading.Channels;

using NeuroViz.Analysis.Detectors;
using NeuroViz.Audio;

namespace NeuroViz.Analysis;

/// <summary>
/// Связывает источник PCM (IAudioPlayer) с набором детекторов и EventSink.
/// Поток данных: PcmAvailable → StftBuffer → foreach detector → EventSink.
/// </summary>
public sealed class AnalyzerPipeline : IDisposable
{
    private readonly StftBuffer _stft;
    private readonly List<IEventDetector> _detectors = new();
    private readonly EventSink _sink;
    private readonly Channel<PcmFrame> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private IAudioPlayer? _player;
    private long _detectorErrors;

    private SpectralFrame? _lastSpectralFrame;

    public AnalyzerPipeline(int sampleRate, int channels, int fftSize = 2048, int hopSize = 1024, EventSink? sink = null)
    {
        _sink = sink ?? new EventSink();
        _stft = new StftBuffer(fftSize, hopSize, sampleRate);

        _queue = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(capacity: 4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _consumer = Task.Run(ConsumerLoopAsync);
    }

    public double LastAnalyzedTimestamp { get; private set; }
    public EventSink Sink => _sink;
    public long DetectorErrors => Interlocked.Read(ref _detectorErrors);

    public void AddDetector(IEventDetector detector) => _detectors.Add(detector);

    public void Attach(IAudioPlayer player)
    {
        if (_player is not null) _player.PcmAvailable -= OnPcm;
        _player = player;
        _player.PcmAvailable += OnPcm;
    }

    public void Detach()
    {
        if (_player is not null) _player.PcmAvailable -= OnPcm;
        _player = null;
        _lastSpectralFrame = null;
    }

    public void Reset()
    {
        foreach (var d in _detectors) d.Reset();
    }

    // ─── AUDIO THREAD ────────────────────────────────────────────────
    private void OnPcm(object? sender, PcmFrame frame)
    {
        // Единственное, что делает аудио-колбэк: бросает фрейм в очередь.
        // Bounded + DropOldest → никогда не блокирует RT-поток.
        _queue.Writer.TryWrite(frame);
    }

    // ─── ANALYZER THREAD ─────────────────────────────────────────────
    private async Task ConsumerLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    LastAnalyzedTimestamp = frame.PositionSeconds;

                    // Сначала сырой PCM (Binaural / Azimuth)
                    foreach (var detector in _detectors)
                    {
                        if (detector is IPcmEventDetector pcmDetector)
                        {
                            try
                            {
                                foreach (var ev in pcmDetector.ProcessPcm(frame))
                                    _sink.Emit(ev, _lastSpectralFrame);
                            }
                            catch { Interlocked.Increment(ref _detectorErrors); }
                        }
                    }

                    // Потом спектр
                    _stft.Push(frame.Samples, frame.Channels, frame.PositionSeconds, ProcessFrame);
                }
                catch { Interlocked.Increment(ref _detectorErrors); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void ProcessFrame(SpectralFrame frame)
    {
        _lastSpectralFrame = frame;
        foreach (var detector in _detectors)
        {
            try
            {
                foreach (var ev in detector.Process(frame))
                    _sink.Emit(ev, frame);
            }
            catch { Interlocked.Increment(ref _detectorErrors); }
        }
    }

    public void Dispose()
    {
        Detach();
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _consumer.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }
}