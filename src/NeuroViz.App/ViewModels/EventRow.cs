using System.Globalization;
using NeuroViz.Analysis;
using NeuroViz.Core.Events;

namespace NeuroViz.App.ViewModels;

public sealed class EventRow
{
    public required double Timestamp { get; init; }
    public required string Type { get; init; }
    public required float Confidence { get; init; }
    public required string ParamsDisplay { get; init; }
    public required AudioEvent Source { get; init; }

    // Поля для Debug-режима
    public required float[] DownsampledSpectrum { get; init; }
    public string SpectrumString => string.Join(",",
        DownsampledSpectrum.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)));

    public static EventRow From(AnalyzedEvent analyzed)
    {
        // Always 32-long — real spectrum when a Frame is present, zeros
        // otherwise. Keeps Source.DownsampledSpectrum and EventRow.DownsampledSpectrum
        // in sync, and makes save path (Events.Select(r => r.Source)) naturally
        // persist the spectrum without special-casing.
        var spectrum = analyzed.Frame != null
            ? Downsample(analyzed.Frame.Magnitudes, 32)
            : new float[32];

        // Source is a copy with DownsampledSpectrum populated. The original
        // analyzed.Event has empty spectrum (live detectors don't attach frames
        // to the AudioEvent they emit — that's the pipeline's job via the sink).
        var source = new AudioEvent
        {
            Type = analyzed.Event.Type,
            TimestampSeconds = analyzed.Event.TimestampSeconds,
            Confidence = analyzed.Event.Confidence,
            Params = analyzed.Event.Params,
            SchemaVersion = analyzed.Event.SchemaVersion,
            SourceLayer = analyzed.Event.SourceLayer,
            StreamId = analyzed.Event.StreamId,
            DownsampledSpectrum = spectrum,
        };

        return new EventRow
        {
            Timestamp = analyzed.Event.TimestampSeconds,
            Type = analyzed.Event.Type,
            Confidence = analyzed.Event.Confidence,
            ParamsDisplay = FormatParams(analyzed.Event.Params),
            Source = source,
            DownsampledSpectrum = spectrum,
        };
    }

    public static EventRow FromStored(AudioEvent ev) => new()
    {
        Timestamp = ev.TimestampSeconds,
        Type = ev.Type,
        Confidence = ev.Confidence,
        ParamsDisplay = FormatParams(ev.Params),
        Source = ev,
        // v1 projects have no Key(7) → ev.DownsampledSpectrum = Array.Empty<float>()
        // → fallback to zeros (same visual as before D4). Re-analyze fills
        // real spectrums and saves as v2.
        DownsampledSpectrum = ev.DownsampledSpectrum.Length > 0
            ? ev.DownsampledSpectrum
            : new float[32],
    };

    private static string FormatParams(Dictionary<string, float> parameters)
    {
        if (parameters.Count == 0) return "";
        return string.Join(", ", parameters.Select(kv =>
            $"{kv.Key}={kv.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
    }

    private static float[] Downsample(float[] mags, int bands)
    {
        var res = new float[bands];
        int step = Math.Max(1, mags.Length / bands);
        for (int i = 0; i < bands; i++)
        {
            int start = i * step;
            int end = Math.Min(mags.Length, start + step);
            float sum = 0;
            for (int j = start; j < end; j++) sum += mags[j];
            res[i] = end > start ? sum / (end - start) : 0;
        }
        return res;
    }
}
