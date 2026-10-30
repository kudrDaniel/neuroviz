using ManagedBass;

namespace NeuroViz.Audio;

/// <summary>
/// Единожды инициализирует Bass. Вызывается до первого плеера.
/// device = -1 => без звукового устройства (полезно в тестах).
/// </summary>
public static class BassBootstrap
{
    private static readonly object _lock = new();
    private static bool _initialized;

    public static void EnsureInitialized(int device = -1, int sampleRate = 44100)
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            if (!Bass.Init(device, sampleRate, DeviceInitFlags.Default))
                throw new InvalidOperationException(
                    $"Bass.Init failed: {Bass.LastError}");

            _initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;
            Bass.Free();
            _initialized = false;
        }
    }
}