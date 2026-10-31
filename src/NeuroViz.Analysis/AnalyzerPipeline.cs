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
    private IAudioPlayer? _player;
    private long _detectorErrors;

    private SpectralFrame? _lastSpectralFrame;

    public double LastAnalyzedTimestamp { get; private set; }

    public AnalyzerPipeline(int sampleRate, int channels, int fftSize = 2048, int hopSize = 1024, EventSink? sink = null)
    {
        _ = channels;
        _sink = sink ?? new EventSink();
        _stft = new StftBuffer(fftSize, hopSize, sampleRate);
    }

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

    private void OnPcm(object? sender, PcmFrame frame)
    {
        LastAnalyzedTimestamp = frame.PositionSeconds;

        // 1. Сначала кормим сырым звуком тех, кому он нужен (Binaural)
        foreach (var detector in _detectors)
        {
            if (detector is IPcmEventDetector pcmDetector)
            {
                try
                {
                    foreach (var ev in pcmDetector.ProcessPcm(frame))
                        _sink.Emit(ev, _lastSpectralFrame); // <-- ПЕРЕДАЕМ ПОСЛЕДНИЙ СПЕКТР
                }
                catch { Interlocked.Increment(ref _detectorErrors); }
            }
        }

        // 2. Затем кормим спектром остальных
        _stft.Push(frame.Samples, frame.Channels, frame.PositionSeconds, ProcessFrame);
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
    }
}