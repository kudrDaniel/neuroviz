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
    public string SpectrumString => string.Join(",", DownsampledSpectrum.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)));

    public static EventRow From(AnalyzedEvent analyzed) => new()
    {
        Timestamp = analyzed.Event.TimestampSeconds,
        Type = analyzed.Event.Type,
        Confidence = analyzed.Event.Confidence,
        ParamsDisplay = analyzed.Event.Params.Count == 0
            ? ""
            : string.Join(", ", analyzed.Event.Params.Select(kv =>
                $"{kv.Key}={kv.Value.ToString("0.###", CultureInfo.InvariantCulture)}")),
        Source = analyzed.Event,

        // Защита от null:
        DownsampledSpectrum = analyzed.Frame != null
            ? Downsample(analyzed.Frame.Magnitudes, 32)
            : new float[32]
    };

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