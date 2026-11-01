using MessagePack;
using NeuroViz.Core.Events;
using NeuroViz.Core.Rules;

namespace NeuroViz.Project;

[MessagePackObject]
public sealed class ProjectDocument
{
    /// <summary>
    /// Максимальная версия схемы, которую понимает текущая сборка.
    /// Инкрементируется при любом несовместимом изменении полей ниже.
    /// </summary>
    public const int CurrentVersion = 1;

    [Key(0)] public int ProjectVersion { get; init; } = CurrentVersion;
    [Key(1)] public string Name { get; init; } = "";
    [Key(2)] public DateTimeOffset CreatedAt { get; init; }
    [Key(3)] public EventTimeline Timeline { get; init; } = new();
    [Key(4)] public RulesDocument Rules { get; init; } = new();
    [Key(5)] public Dictionary<string, string> Meta { get; init; } = new();
}