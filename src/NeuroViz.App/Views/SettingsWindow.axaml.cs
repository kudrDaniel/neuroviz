using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NeuroViz.Core.Rules;

namespace NeuroViz.App.Views;

/// <summary>
/// Settings dialog for <see cref="AppSettings"/>. Works on a local copy —
/// Cancel discards everything, Save validates all three fields and only
/// then copies back into the passed-in AppSettings and calls Save().
///
/// <para>Empty field = valid ("use bundled / defer validation to
/// consumer"). Non-empty field = must pass the field's validator.</para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _target;

    /// <summary>Parameterless ctor for the Avalonia designer.</summary>
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings target)
    {
        InitializeComponent();
        _target = target;

        // Seed with current values — user sees the active config.
        GodotExeBox.Text = target.GodotExePath ?? "";
        GodotProjectBox.Text = target.GodotProjectPath ?? "";
        RulesPathBox.Text = target.RulesPath ?? "";
    }

    private async void OnBrowseGodotExe(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate Godot .NET executable (mono build)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Godot executable") { Patterns = new[] { "Godot*.exe" } },
            },
        });
        if (files.Count > 0 && files[0].Path.LocalPath is { } path)
            GodotExeBox.Text = path;
    }

    private async void OnBrowseGodotProject(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate NeuroViz.Visualizer folder (must contain project.godot)",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { } path)
            GodotProjectBox.Text = path;
    }

    private async void OnBrowseRules(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select rules JSON",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Rules JSON") { Patterns = new[] { "*.json" } },
            },
        });
        if (files.Count > 0 && files[0].Path.LocalPath is { } path)
            RulesPathBox.Text = path;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screens = Screens.All;
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            MonitorBox.Items.Add($"Monitor {i + 1} ({s.Bounds.Width}×{s.Bounds.Height})");
        }

        var idx = _target.VisualizerMonitorIndex;
        if (idx < 0 || idx >= screens.Count) idx = 0;
        if (MonitorBox.ItemCount > 0) MonitorBox.SelectedIndex = idx;

        AlwaysOnTopBox.IsChecked = _target.VisualizerAlwaysOnTop;

        CameraOrbitSlider.Value = Math.Clamp(_target.CameraOrbitSpeed, 0.01, 0.2);
        CameraYSlider.Value = Math.Clamp(_target.CameraYAmplitude, 0.0, 1.0);
        AmbientSlider.Value = Math.Clamp(_target.AmbientParticleCount, 20, 1000);
        BurstSlider.Value = Math.Clamp(_target.BurstParticleCount, 20, 500);
    }

    private void OnResetDefaults(object? sender, RoutedEventArgs e)
    {
        GodotExeBox.Text = "";
        GodotProjectBox.Text = "";
        RulesPathBox.Text = "";
        MonitorBox.SelectedIndex = 0;
        AlwaysOnTopBox.IsChecked = false;
        CameraOrbitSlider.Value = 0.05;
        CameraYSlider.Value = 0.4;
        AmbientSlider.Value = 400;
        BurstSlider.Value = 120;
        HideAllErrors();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var exePath = NormalizeOrNull(GodotExeBox.Text);
        var projectPath = NormalizeOrNull(GodotProjectBox.Text);
        var rulesPath = NormalizeOrNull(RulesPathBox.Text);

        var exeErr = ValidateGodotExe(exePath);
        var projErr = ValidateGodotProject(projectPath);
        var rulesErr = ValidateRulesPath(rulesPath);

        SetError(GodotExeError, exeErr);
        SetError(GodotProjectError, projErr);
        SetError(RulesPathError, rulesErr);

        if (exeErr is not null || projErr is not null || rulesErr is not null)
            return; // stay open, user fixes the field

        // Validation passed — mutate the shared AppSettings and persist.
        _target.GodotExePath = exePath;
        _target.GodotProjectPath = projectPath;
        _target.RulesPath = rulesPath;
        _target.VisualizerMonitorIndex = Math.Max(0, MonitorBox.SelectedIndex);
        _target.VisualizerAlwaysOnTop = AlwaysOnTopBox.IsChecked ?? false;
        _target.CameraOrbitSpeed = (float)CameraOrbitSlider.Value;
        _target.CameraYAmplitude = (float)CameraYSlider.Value;
        _target.AmbientParticleCount = (int)AmbientSlider.Value;
        _target.BurstParticleCount = (int)BurstSlider.Value;
        _target.Save();
        Close(true);
    }

    // ── Validators ──────────────────────────────────────────────────

    private static string? ValidateGodotExe(string? path)
    {
        if (path is null) return null;   // empty = valid (Launch will prompt)
        return File.Exists(path) ? null : "File not found.";
    }

    private static string? ValidateGodotProject(string? path)
    {
        if (path is null) return null;
        if (!Directory.Exists(path))
            return "Folder not found.";
        if (!File.Exists(Path.Combine(path, "project.godot")))
            return "project.godot not found in this folder.";
        return null;
    }

    private static string? ValidateRulesPath(string? path)
    {
        if (path is null) return null;
        if (!File.Exists(path))
            return "File not found.";
        try
        {
            RulesLoader.FromFile(path);   // full parse + structural validation
            return null;
        }
        catch (Exception ex)
        {
            return $"Invalid rules: {ex.Message}";
        }
    }

    // ── UI helpers ──────────────────────────────────────────────────

    private static string? NormalizeOrNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void SetError(TextBlock target, string? message)
    {
        if (message is null)
        {
            target.IsVisible = false;
            target.Text = "";
        }
        else
        {
            target.Text = message;
            target.IsVisible = true;
        }
    }

    private void HideAllErrors()
    {
        SetError(GodotExeError, null);
        SetError(GodotProjectError, null);
        SetError(RulesPathError, null);
    }
}
