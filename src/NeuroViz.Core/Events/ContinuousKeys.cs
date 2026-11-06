namespace NeuroViz.Core.Events;

/// <summary>
/// Ordered list of continuous-metric keys persisted in .nvproj.
/// The order is a WIRE CONTRACT for <see cref="ContinuousFrame.Values"/>:
/// changing it requires a schema bump (CurrentVersion + SchemaVersion +
/// migration). Add new keys ONLY at the end — the array is positional.
/// </summary>
public static class ContinuousKeys
{
    public const string SpectralCentroid = "spectral_centroid";
    public const string Harmonicity = "harmonicity";
    public const string BandEnergyLow = "band_energy.low";
    public const string BandEnergyMid = "band_energy.mid";
    public const string BandEnergyHigh = "band_energy.high";
    public const string VadState = "vad_state";
    public const string SpeechMusicRatio = "speech_music_ratio";
    public const string SpectralFlux = "spectral_flux";

    public static readonly string[] Ordered = new[]
    {
        SpectralCentroid,
        Harmonicity,
        BandEnergyLow,
        BandEnergyMid,
        BandEnergyHigh,
        VadState,
        SpeechMusicRatio,
        SpectralFlux,
    };

    public const int Count = 8;
}
