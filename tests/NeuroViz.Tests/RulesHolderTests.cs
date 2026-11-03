using NeuroViz.Core.Events;
using NeuroViz.Core.Rules;

namespace NeuroViz.Tests;

public class RulesHolderTests
{
    private const string LowThresholdRules = """
        {
          "version": 1,
          "rules": [ {
            "id": "onset-pulse", "priority": 100,
            "when": { "event": "onset", "filters": { "confidence": { "min": 0.3 } } },
            "actions": [ { "type": "set", "param": "pulse", "value": 1.0, "decayMs": 150 } ]
          } ]
        }
        """;

    private const string HighThresholdRules = """
        {
          "version": 1,
          "rules": [ {
            "id": "onset-pulse", "priority": 100,
            "when": { "event": "onset", "filters": { "confidence": { "min": 0.9 } } },
            "actions": [ { "type": "set", "param": "pulse", "value": 1.0, "decayMs": 150 } ]
          } ]
        }
        """;

    [Fact]
    public void Reload_PicksUpFileChanges()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rules.json");
        File.WriteAllText(path, LowThresholdRules);

        var holder = new RulesHolder(RulesLoader.FromFile(path), path);
        var ev = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.7f };

        // 0.7 > 0.3 → правило срабатывает.
        Assert.NotEmpty(holder.Engine.Process(ev, 0));

        File.WriteAllText(path, HighThresholdRules);
        // Defeat filesystem timestamp granularity in case it's ever used.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        Assert.True(holder.TryReload());

        // 0.7 < 0.9 → правило не срабатывает.
        Assert.Empty(holder.Engine.Process(ev, 0));
    }

    [Fact]
    public void Reload_MissingFile_Fails_KeepsOldRules()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rules.json");
        File.WriteAllText(path, LowThresholdRules);
        var holder = new RulesHolder(RulesLoader.FromFile(path), path);

        File.Delete(path);

        Assert.False(holder.TryReload());
        Assert.NotNull(holder.LastError);

        // Старый движок жив.
        Assert.NotEmpty(holder.Engine.Process(
            new AudioEvent { Type = EventTypes.Onset, Confidence = 0.7f }, 0));
    }

    [Fact]
    public void Reload_CorruptFile_Fails_KeepsOldRules()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rules.json");
        File.WriteAllText(path, LowThresholdRules);
        var holder = new RulesHolder(RulesLoader.FromFile(path), path);

        File.WriteAllText(path, "{ not valid json");
        Assert.False(holder.TryReload());
        Assert.NotNull(holder.LastError);

        Assert.NotEmpty(holder.Engine.Process(
            new AudioEvent { Type = EventTypes.Onset, Confidence = 0.7f }, 0));
    }

    [Fact]
    public void ParameterlessReload_NoSourcePath_Fails()
    {
        var holder = new RulesHolder(
            RulesLoader.FromJson("""{ "version": 1, "rules": [] }"""),
            sourcePath: null);

        Assert.False(holder.TryReload());
        Assert.NotNull(holder.LastError);
    }

    [Fact]
    public void ExplicitPathReload_Works_WhenHolderHasNoSource()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "rules.json");
        File.WriteAllText(path, LowThresholdRules);

        // Simulates the VM fallback: SourcePath == null, explicit path given.
        var holder = new RulesHolder(
            RulesLoader.FromJson("""{ "version": 1, "rules": [] }"""),
            sourcePath: null);

        var ev = new AudioEvent { Type = EventTypes.Onset, Confidence = 0.5f };
        Assert.Empty(holder.Engine.Process(ev, 0));

        Assert.True(holder.TryReload(path));
        Assert.NotEmpty(holder.Engine.Process(ev, 0));
        Assert.Equal(path, holder.SourcePath);
    }

    [Fact]
    public void SetDocument_RebuildsEngine_AndClearsLastError()
    {
        var holder = new RulesHolder(
            RulesLoader.FromJson("""{ "version": 1, "rules": [] }"""),
            sourcePath: null);
        Assert.Empty(holder.Engine.Process(
            new AudioEvent { Type = EventTypes.Onset, Confidence = 0.9f }, 0));

        holder.SetDocument(RulesLoader.FromJson(LowThresholdRules), sourcePath: null);

        Assert.NotEmpty(holder.Engine.Process(
            new AudioEvent { Type = EventTypes.Onset, Confidence = 0.9f }, 0));
        Assert.Null(holder.LastError);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nv-a4-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
