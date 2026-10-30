using System.Linq;

using NeuroViz.Core.Events;

namespace NeuroViz.App.ViewModels;

public sealed class EventRow
{
    public required double Timestamp { get; init; }
    public required string Type { get; init; }
    public required float Confidence { get; init; }
    public required string ParamsDisplay { get; init; }
    public required AudioEvent Source { get; init; }

    public static EventRow From(AudioEvent ev) => new()
    {
        Timestamp = ev.TimestampSeconds,
        Type = ev.Type,
        Confidence = ev.Confidence,
        ParamsDisplay = ev.Params.Count == 0
            ? ""
            : string.Join(", ", ev.Params.Select(kv =>
                $"{kv.Key}={kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}")),
        Source = ev,
    };
}