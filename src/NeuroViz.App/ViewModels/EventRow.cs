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

    public static EventRow From(AnalyzedEvent analyzed) => new()
    {
        Timestamp = analyzed.Event.TimestampSeconds,
        Type = analyzed.Event.Type,
        Confidence = analyzed.Event.Confidence,
        ParamsDisplay = FormatParams(analyzed.Event.Params),
        Source = analyzed.Event,
        DownsampledSpectrum = analyzed.Frame != null
            ? Downsample(analyzed.Frame.Magnitudes, 32)
            : new float[32],
    };

    /// <summary>
    /// Событие, восстановленное из .nvproj: нет SpectralFrame (спектр
    /// не сохраняется), DownsampledSpectrum — нули. Re-analyze (MVP-D)
    /// заполнит его заново.
    /// </summary>
    public static EventRow FromStored(AudioEvent ev) => new()
    {
        Timestamp = ev.TimestampSeconds,
        Type = ev.Type,
        Confidence = ev.Confidence,
        ParamsDisplay = FormatParams(ev.Params),
        Source = ev,
        DownsampledSpectrum = new float[32],
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