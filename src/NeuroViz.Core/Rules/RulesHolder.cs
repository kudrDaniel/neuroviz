namespace NeuroViz.Core.Rules;

/// <summary>
/// Holds the currently-active rules document, the engine built from it,
/// and (optionally) the path they were loaded from. Swap is atomic from
/// the caller's perspective: either the engine is rebuilt successfully,
/// or the previous one stays intact.
///
/// <para><see cref="SourcePath"/> == null means "not backed by a file"
/// (e.g. rules came embedded in a .nvproj). <see cref="TryReload()"/>
/// no-ops in that case — callers that want a fallback should resolve a
/// path themselves and call <see cref="TryReload(string)"/>.</para>
/// </summary>
public sealed class RulesHolder
{
    private RulesDocument _document;
    private RuleEngine _engine;

    public RulesHolder(RulesDocument document, string? sourcePath = null)
    {
        _document = document;
        _engine = new RuleEngine(document);
        SourcePath = sourcePath;
    }

    public RulesDocument Document => _document;
    public RuleEngine Engine => _engine;
    public string? SourcePath { get; private set; }

    /// <summary>Human-readable reason for the last failed reload, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Replaces the active document and rebuilds the engine.
    /// Pass <paramref name="sourcePath"/> = null for project-embedded rules.
    /// </summary>
    public void SetDocument(RulesDocument document, string? sourcePath)
    {
        _document = document;
        _engine = new RuleEngine(document);
        SourcePath = sourcePath;
        LastError = null;
    }

    /// <summary>Reloads from <see cref="SourcePath"/>.</summary>
    public bool TryReload()
    {
        if (string.IsNullOrEmpty(SourcePath))
        {
            LastError = "No source path — rules were loaded from a project";
            return false;
        }
        return TryReload(SourcePath);
    }

    /// <summary>
    /// Reloads from an explicit path. On success <see cref="SourcePath"/>
    /// becomes <paramref name="path"/> (so subsequent parameterless reloads
    /// keep working). On failure the current rules are kept and
    /// <see cref="LastError"/> is set.
    /// </summary>
    public bool TryReload(string path)
    {
        if (!File.Exists(path))
        {
            LastError = $"Rules file not found: {path}";
            return false;
        }

        try
        {
            var doc = RulesLoader.FromFile(path);
            SetDocument(doc, path);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }
}
