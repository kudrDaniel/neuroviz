using NWaves.Transforms;

namespace NeuroViz.Analysis;

/// <summary>
/// Накопитель PCM-сэмплов и производитель SpectralFrame-ов.
/// Один экземпляр на трек. Не потокобезопасен: вызывается только из одного
/// аудио-потока (или из UI-потока, если анализируем оффлайн).
/// </summary>
public sealed class StftBuffer
{
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _sampleRate;
    private readonly float[] _window;
    private readonly float[] _ring;
    private readonly int _mask;
    private readonly Fft _fft;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _mags;

    private int _writePos;
    private long _count;
    private long _frameSeq;

    public StftBuffer(int fftSize, int hopSize, int sampleRate)
    {
        if ((fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("fftSize must be a power of 2", nameof(fftSize));
        if (hopSize <= 0 || hopSize > fftSize)
            throw new ArgumentException("hopSize must be in (0, fftSize]", nameof(hopSize));

        _fftSize = fftSize;
        _hopSize = hopSize;
        _sampleRate = sampleRate;

        _window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));

        _ring = new float[fftSize];
        _mask = fftSize - 1;

        _fft = new Fft(fftSize);
        _re = new float[fftSize];
        _im = new float[fftSize];
        _mags = new float[fftSize / 2 + 1];
    }

    /// <summary>
    /// Принимает interleaved PCM (channels > 1 усредняется в моно).
    /// endTimestampSeconds — позиция в треке ПОСЛЕДНЕГО сэмпла пуша
    /// (совпадает с PcmFrame.PositionSeconds).
    /// Каждый SpectralFrame получает корректный timestamp центра своего FFT-окна.
    /// </summary>
    public void Push(
        ReadOnlySpan<float> interleaved,
        int channels,
        double endTimestampSeconds,
        Action<SpectralFrame> onFrame)
    {
        if (channels <= 0) throw new ArgumentException("channels must be > 0", nameof(channels));

        var monoFrames = interleaved.Length / channels;
        if (monoFrames == 0) return;
        var invCh = 1f / channels;

        var lastIndex = monoFrames - 1;
        var sr = _sampleRate;

        for (int i = 0; i < monoFrames; i++)
        {
            var mono = 0f;
            for (int c = 0; c < channels; c++)
                mono += interleaved[i * channels + c];
            mono *= invCh;

            _ring[_writePos & _mask] = mono;
            _writePos++;
            _count++;

            if (_count >= _fftSize && (_count - _fftSize) % _hopSize == 0)
            {
                // Окно FFT заканчивается на текущем сэмпле (i).
                // Позиция последнего сэмпла окна:
                var frameEndPos = endTimestampSeconds - (lastIndex - i) / (double)sr;
                // Центр окна — на fftSize/2 сэмплов раньше:
                var frameCenterPos = frameEndPos - (_fftSize / 2) / (double)sr;

                var frame = ProcessFrame(frameCenterPos);
                onFrame(frame);
            }
        }
    }

    private SpectralFrame ProcessFrame(double timestamp)
    {
        for (int i = 0; i < _fftSize; i++)
        {
            var idx = (_writePos - _fftSize + i) & _mask;
            _re[i] = _ring[idx] * _window[i];
            _im[i] = 0f;
        }

        _fft.Direct(_re, _im);

        for (int i = 0; i < _mags.Length; i++)
            _mags[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

        return new SpectralFrame
        {
            Magnitudes = (float[])_mags.Clone(),
            TimestampSeconds = timestamp,
            SampleRate = _sampleRate,
            FftSize = _fftSize,
            Sequence = _frameSeq++,
        };
    }
}