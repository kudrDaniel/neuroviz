using System.Text.Json.Serialization;

namespace NeuroViz.Core.Rules;

public sealed class RulesDocument
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("rules")] public List<Rule> Rules { get; init; } = new();
}

public sealed class Rule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("priority")] public int Priority { get; init; }
    [JsonPropertyName("when")] public RuleCondition When { get; init; } = new();
    [JsonPropertyName("actions")] public List<RuleAction> Actions { get; init; } = new();
}

public sealed class RuleCondition
{
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("filters")] public Dictionary<string, Filter> Filters { get; init; } = new();
}

public sealed class Filter
{
    [JsonPropertyName("min")] public float? Min { get; init; }
    [JsonPropertyName("max")] public float? Max { get; init; }
    [JsonPropertyName("eq")]  public string? Eq { get; init; }   // для строковых params
}

public sealed class RuleAction
{
    [JsonPropertyName("type")] public string Type { get; init; } = "set";
    [JsonPropertyName("param")] public string Param { get; init; } = "";
    [JsonPropertyName("value")] public float Value { get; init; }
    [JsonPropertyName("decayMs")] public int DecayMs { get; init; }
}