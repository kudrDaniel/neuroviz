using NeuroViz.Core.Events;
using NeuroViz.Audio;

namespace NeuroViz.Analysis.Detectors;

/// <summary>
/// Детектор событий слоя 1. Process вызывается на каждый SpectralFrame
/// последовательно, из одного потока. Детектор может хранить состояние
/// (история спектров, счётчики) между вызовами.
/// </summary>
public interface IEventDetector
{
    IEnumerable<AudioEvent> Process(SpectralFrame frame);

    void Reset();
}

public interface IPcmEventDetector : IEventDetector
{
    IEnumerable<AudioEvent> ProcessPcm(PcmFrame frame);
}