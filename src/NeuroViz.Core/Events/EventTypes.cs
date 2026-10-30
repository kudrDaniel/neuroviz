namespace NeuroViz.Core.Events;

public static class EventTypes
{
    // Layer 1
    public const string Onset = "onset";
    public const string Offset = "offset";
    public const string PitchSalience = "pitch_salience";
    public const string Harmonicity = "harmonicity";
    public const string BandEnergy = "band_energy";          // params: band=low|mid|high|..., value
    public const string SpectralFlux = "spectral_flux";
    public const string AzimuthEstimate = "azimuth_estimate"; // params: azimuth_rad
    public const string VadState = "vad_state";              // params: state=0|1
    public const string SpeechMusicRatio = "speech_music_ratio";

    // Layer 2
    public const string Novelty = "novelty";                 // params: error_magnitude
    public const string StreamBoundary = "stream_boundary";  // params: open=0|1
    public const string MotifRepetition = "motif_repetition"; // params: similarity
}