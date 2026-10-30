using NeuroViz.Core.Events;

namespace NeuroViz.Core.Rules;

public sealed class RuleEngine
{
    private readonly List<Rule> _rules;

    public RuleEngine(RulesDocument doc)
        => _rules = doc.Rules.OrderByDescending(r => r.Priority).ToList();

    // Применяет правило к событию и возвращает набор действий (с timestamps для decay)
    public IEnumerable<ResolvedAction> Process(AudioEvent ev, double nowSeconds)
    {
        foreach (var rule in _rules)
        {
            if (rule.When.Event != ev.Type) continue;
            if (!Matches(rule.When.Filters, ev)) continue;

            foreach (var action in rule.Actions)
                yield return new ResolvedAction(rule.Id, action, nowSeconds);
        }
    }

    private static bool Matches(Dictionary<string, Filter> filters, AudioEvent ev)
    {
        foreach (var (key, f) in filters)
        {
            if (!TryGet(ev, key, out var raw)) return false;
            if (f.Eq is not null && !string.Equals(raw, f.Eq, StringComparison.Ordinal)) return false;
            if (f.Min.HasValue || f.Max.HasValue)
            {
                if (!float.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
                if (f.Min.HasValue && v < f.Min.Value) return false;
                if (f.Max.HasValue && v > f.Max.Value) return false;
            }
        }
        return true;
    }

    private static bool TryGet(AudioEvent ev, string key, out string value)
    {
        switch (key)
        {
            case "confidence": value = ev.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case "timestamp":  value = ev.TimestampSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case "type":       value = ev.Type; return true;
        }
        if (key.StartsWith("params.", StringComparison.Ordinal))
        {
            var pk = key[7..];
            if (ev.Params.TryGetValue(pk, out var f))
            {
                value = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
        }
        value = "";
        return false;
    }
}

public sealed record ResolvedAction(string RuleId, RuleAction Action, double AtSeconds);