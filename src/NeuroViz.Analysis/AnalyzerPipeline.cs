using System.Threading.Channels;
using NeuroViz.Analysis.Detectors;
using NeuroViz.Audio;

namespace NeuroViz.Analysis;

/// <summary>
/// Связывает источник PCM (IAudioPlayer) с набором детекторов и EventSink.
/// Поток данных: PcmAvailable → bounded Channel → analyzer-thread →
/// StftBuffer → foreach detector → EventSink.
///
/// Аудио-колбэк только кладёт кадр в очередь (TryWrite). Вся тяжёлая работа —
/// STFT, FFT, детекторы — выполняется на отдельном потоке, чтобы не
/// конкурировать с real-time рендерингом BASS.
/// </summary>
public sealed class AnalyzerPipeline : IDisposable
{
    private const int QueueCapacity = 8;

    private readonly StftBuffer _stft;
    private readonly List<IEventDetector> _detectors = new();
    private readonly EventSink _sink;
    private readonly Channel<PcmFrame> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private IAudioPlayer? _player;
    private long _detectorErrors;

    private SpectralFrame? _lastSpectralFrame;
    private double _lastAnalyzedTimestamp;

    public AnalyzerPipeline(int sampleRate, int channels, int fftSize = 2048, int hopSize = 1024, EventSink? sink = null)
    {
        _ = channels;
        _sink = sink ?? new EventSink();
        _stft = new StftBuffer(fftSize, hopSize, sampleRate);

        _queue = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,   // BASS не даёт формальной гарантии одного потока
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _consumer = Task.Run(ConsumerLoopAsync);
    }

    /// <summary>Позиция последнего обработанного кадра. Читается из UI-потока.</summary>
    public double LastAnalyzedTimestamp => Volatile.Read(ref _lastAnalyzedTimestamp);

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

    // ───────────────────────── AUDIO THREAD ─────────────────────────
    private void OnPcm(object? sender, PcmFrame frame)
    {
        // Единственное, что делает аудио-колбэк: не блокируя, кладёт кадр
        // в ограниченную очередь. При переполнении вытесняется самый
        // старый кадр — real-time поток BASS не страдает.
        _queue.Writer.TryWrite(frame);
    }

    // ──────────────────────── ANALYZER THREAD ───────────────────────
    private async Task ConsumerLoopAsync()
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    Volatile.Write(ref _lastAnalyzedTimestamp, frame.PositionSeconds);

                    // 1. Сначала сырым звуком — тех, кому он нужен (Binaural/Azimuth).
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

                    // 2. Затем спектром — остальных.
                    _stft.Push(frame.Samples, frame.Channels, frame.PositionSeconds, ProcessFrame);
                }
                catch
                {
                    Interlocked.Increment(ref _detectorErrors);
                }
            }
        }
        catch (OperationCanceledException) { /* штатное завершение по Dispose */ }
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
            catch
            {
                Interlocked.Increment(ref _detectorErrors);
            }
        }
    }

    public void Dispose()
    {
        Detach();
        _cts.Cancel();

        // Даём consumer'у шанс завершиться штатно; не блокируем закрытие окна
        // дольше секунды.
        try { _consumer.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }

        _cts.Dispose();
    }
}