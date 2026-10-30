namespace NeuroViz.Audio;

public interface IAudioPlayer : IDisposable
{
    void Load(string path);
    void Play();
    void Pause();
    void Stop();
    void Seek(double seconds);

    double Position { get; }
    double Duration { get; }
    bool IsPlaying { get; }
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Событие из аудио-колбэка. Не блокирующее, вызывается на аудио-потоке.
    /// Подписчики должны быть быстрыми (без аллокаций в идеале).
    /// </summary>
    event EventHandler<PcmFrame>? PcmAvailable;
}