using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroViz.App;

/// <summary>
/// Пользовательские настройки NeuroViz.App. Хранятся в
/// %APPDATA%/NeuroViz/settings.json — переживают dotnet clean и
/// переустановку приложения.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Путь к исполняемому файлу Godot .NET (mono-сборка).</summary>
    public string? GodotExePath { get; set; }

    /// <summary>Путь к папке проекта Godot (должна содержать project.godot).</summary>
    public string? GodotProjectPath { get; set; }

    /// <summary>Явный путь к JSON-файлу правил. null → встроенный basic.json.</summary>
    public string? RulesPath { get; set; }

    /// <summary>
    /// Which monitor to place the Visualizer fullscreen on (0-based).
    /// Applies at Launch; requires a monitor that exists at that moment.
    /// 0 = primary. Ignored if the index is out of range.
    /// </summary>
    public int VisualizerMonitorIndex { get; set; }

    /// <summary>
    /// Keep the Visualizer window always on top of other windows.
    /// </summary>
    public bool VisualizerAlwaysOnTop { get; set; }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NeuroViz",
            "settings.json");

    public static AppSettings Load()
    {
        var path = DefaultPath;
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? new AppSettings();
        }
        catch
        {
            // Битый settings.json не должен блокировать запуск.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Persistence — best-effort. Не валим UI из-за прав доступа.
        }
    }
}
