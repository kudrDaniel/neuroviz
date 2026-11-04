using NeuroViz.Analysis;
using NeuroViz.Analysis.Detectors;

namespace NeuroViz.Tests.Analysis;

public class SpectralFluxDetectorTests
{
    [Fact]
    public void FirstFrame_EmitsZero()
    {
        // No previous frame to compare against — baseline 0, no flash.
        var sink = new EventSink();
        new SpectralFluxDetector().ProcessContinuous(MakeFrame(0.5f), sink);
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void SteadySpectrum_EmitsZero()
    {
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        det.ProcessContinuous(MakeFrame(0.5f), sink);   // warm-up: 0
        sink.DrainContinuous();

        det.ProcessContinuous(MakeFrame(0.5f), sink);   // no change: 0
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void RisingSpectrum_EmitsPositive()
    {
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        det.ProcessContinuous(MakeFrame(0.0f), sink);
        sink.DrainContinuous();

        // Silent → loud: risingFlux = totalMag → relative flux = 1.0.
        det.ProcessContinuous(MakeFrame(1.0f), sink);
        Assert.Equal(1.0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void FallingSpectrum_EmitsZero()
    {
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        det.ProcessContinuous(MakeFrame(1.0f), sink);
        sink.DrainContinuous();

        // Loud → silent: only rising change counts, none → 0.
        det.ProcessContinuous(MakeFrame(0.0f), sink);
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void RelativeFlux_IndependentOfLoudness()
    {
        // Quiet rise (0.1 → 0.2) and loud rise (1.0 → 2.0) are the same
        // relative change (both double) → same relative flux.
        var qDet = new SpectralFluxDetector();
        var qSink = new EventSink();
        qDet.ProcessContinuous(MakeFrame(0.1f), qSink);
        qSink.DrainContinuous();
        qDet.ProcessContinuous(MakeFrame(0.2f), qSink);
        var quiet = qSink.DrainContinuous()["spectral_flux"];

        var lDet = new SpectralFluxDetector();
        var lSink = new EventSink();
        lDet.ProcessContinuous(MakeFrame(1.0f), lSink);
        lSink.DrainContinuous();
        lDet.ProcessContinuous(MakeFrame(2.0f), lSink);
        var loud = lSink.DrainContinuous()["spectral_flux"];

        // Both are 0.5 by construction: risingFlux/totalMag = 1.0/2.0.
        Assert.Equal(0.5f, quiet, 4);
        Assert.Equal(0.5f, loud, 4);
    }

    [Fact]
    public void Silence_EmitsZero()
    {
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        // Establish non-silent prevMags, then silence.
        det.ProcessContinuous(MakeFrame(0.5f), sink);
        sink.DrainContinuous();
        det.ProcessContinuous(MakeFrame(0.0f), sink);
        // Falling to silence → 0 (no rising change).
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void Reset_ClearsPrevMags()
    {
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        det.ProcessContinuous(MakeFrame(0.5f), sink);
        sink.DrainContinuous();
        det.ProcessContinuous(MakeFrame(1.0f), sink);
        var beforeReset = sink.DrainContinuous()["spectral_flux"];
        Assert.True(beforeReset > 0f, "sanity: had flux before reset");

        det.Reset();

        // After reset, first frame behaves like warm-up → 0.
        det.ProcessContinuous(MakeFrame(1.0f), sink);
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    [Fact]
    public void FftSizeChange_TreatedAsWarmUp()
    {
        // If FFT size changes mid-stream (e.g. project swap), the frame
        // magnitude array length changes. The detector must not compare
        // mismatched arrays — treat as warm-up, emit 0.
        var det = new SpectralFluxDetector();
        var sink = new EventSink();

        det.ProcessContinuous(MakeFrame(0.5f, fftSize: 2048), sink);
        sink.DrainContinuous();

        det.ProcessContinuous(MakeFrame(1.0f, fftSize: 1024), sink);
        Assert.Equal(0f, sink.DrainContinuous()["spectral_flux"]);
    }

    private static SpectralFrame MakeFrame(float magnitude, int fftSize = 2048)
    {
        var mags = new float[fftSize / 2 + 1];
        Array.Fill(mags, magnitude);
        return new SpectralFrame
        {
            Magnitudes = mags,
            TimestampSeconds = 0,
            SampleRate = 44100,
            FftSize = fftSize,
            Sequence = 0,
        };
    }
}
