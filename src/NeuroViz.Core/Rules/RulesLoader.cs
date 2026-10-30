using System.Text.Json;

namespace NeuroViz.Core.Rules;

public static class RulesLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RulesDocument FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<RulesDocument>(json, Options);
        if (doc is null) throw new InvalidDataException("Empty rules document");
        Validate(doc);
        return doc;
    }

    public static RulesDocument FromFile(string path)
        => FromJson(File.ReadAllText(path));

    private static void Validate(RulesDocument doc)
    {
        var seen = new HashSet<string>();
        foreach (var r in doc.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id))
                throw new InvalidDataException("Rule id required");
            if (!seen.Add(r.Id))
                throw new InvalidDataException($"Duplicate rule id: {r.Id}");
            if (string.IsNullOrWhiteSpace(r.When.Event))
                throw new InvalidDataException($"Rule {r.Id}: when.event required");
            if (r.Actions.Count == 0)
                throw new InvalidDataException($"Rule {r.Id}: at least one action required");
        }
    }
}