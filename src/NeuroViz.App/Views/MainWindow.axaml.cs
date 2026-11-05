using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using NeuroViz.App.ViewModels;
using NeuroViz.Project;

namespace NeuroViz.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        PositionSlider.AddHandler(Thumb.DragStartedEvent, OnSliderDragStarted, RoutingStrategies.Bubble);
        PositionSlider.AddHandler(Thumb.DragCompletedEvent, OnSliderDragCompleted, RoutingStrategies.Bubble);

        // D1: keyboard shortcuts. Tunnel-phase handler runs before child
        // controls — Slider would otherwise consume ←/→ for value adjustment,
        // DataGrid would consume arrows/Home for cell navigation, Button would
        // consume Space for activation. Tunnel + e.Handled = true pre-empts
        // all three, matching standard media-player UX.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Button activates on Space KeyUp (ButtonBase: KeyDown sets IsPressed,
        // KeyUp fires Click). Without swallowing KeyUp, a Button that still has
        // focus from a prior mouse click re-fires on every Space — Play button
        // resumes playback after Space paused it; Launch Visualizer opens a
        // second Godot window.
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Skip if typing in a text field — Space/arrows must work there.
        if (e.Source is TextBox) return;

        // Skip while dragging the position slider — a concurrent seek would
        // fight the drag and feel broken.
        var vm = ViewModel;
        if (vm.IsDragging) return;

        switch (e.Key)
        {
            case Key.Space:
                vm.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                vm.SeekBackwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.SeekForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Home:
                vm.SeekHomeCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;

        // Swallow Space KeyUp unconditionally (no command) — Play/Pause already
        // fired on KeyDown. Buttons activate on KeyUp; without this, focused
        // buttons re-click on Space. Enter is intentionally NOT swallowed:
        // keyboard activation of buttons via Tab+Enter keeps working.
        if (e.Key == Key.Space)
            e.Handled = true;
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var oldRulesPath = ViewModel.Settings.RulesPath;
        var dialog = new SettingsWindow(ViewModel.Settings);
        var saved = await dialog.ShowDialog<bool>(this);
        if (!saved) return;

        if (!string.Equals(ViewModel.Settings.RulesPath, oldRulesPath, StringComparison.Ordinal))
        {
            ViewModel.StatusText =
                "Settings saved. Rules path changed — click Reload Rules to apply.";
        }
        else
        {
            ViewModel.StatusText = "Settings saved.";
        }
    }

    private async void OnLaunchVisualizerClicked(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;

        // 1. Godot EXE — валидируем или запрашиваем.
        if (string.IsNullOrEmpty(vm.Settings.GodotExePath) || !File.Exists(vm.Settings.GodotExePath))
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Locate Godot .NET executable (mono build)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                new FilePickerFileType("Godot .NET executable")
                {
                    Patterns = new[] { "Godot*.exe" },
                },
            },
            });

            if (files.Count == 0)
            {
                vm.StatusText = "Visualizer: Godot executable not selected";
                return;
            }

            vm.Settings.GodotExePath = files[0].Path.LocalPath;
            vm.Settings.Save();
        }

        // 2. Godot project — валидируем или запрашиваем.
        var projectOk = !string.IsNullOrEmpty(vm.Settings.GodotProjectPath)
            && Directory.Exists(vm.Settings.GodotProjectPath)
            && File.Exists(Path.Combine(vm.Settings.GodotProjectPath, "project.godot"));

        if (!projectOk)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Locate NeuroViz.Visualizer folder (must contain project.godot)",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
            {
                vm.StatusText = "Visualizer: Godot project folder not selected";
                return;
            }

            vm.Settings.GodotProjectPath = folders[0].Path.LocalPath;
            vm.Settings.Save();
        }

        // 3. Запуск.
        try
        {
            vm.LaunchVisualizer();
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Visualizer launch failed: {ex.Message}";
        }
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio files")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" },
                },
            },
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { } local)
            await ViewModel.LoadFileAsync(local);
    }

    private async void OnLoadProjectClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open NeuroViz project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
            new FilePickerFileType("NeuroViz project")
            {
                Patterns = new[] { "*.nvproj" },
            },
        },
        });

        if (files.Count == 0) return;
        await LoadProjectWithAudioResolutionAsync(files[0].Path.LocalPath);
    }

    /// <summary>
    /// Пытается загрузить проект; при отсутствии аудио спрашивает
    /// пользователя и повторяет с overrideAudioPath.
    /// </summary>
    private async Task LoadProjectWithAudioResolutionAsync(
        string projectPath, string? overrideAudioPath = null)
    {
        try
        {
            await ViewModel.LoadProjectAsync(projectPath, overrideAudioPath);
        }
        catch (AudioNotFoundException)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Locate audio file for this project",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" },
                },
            },
            });

            if (files.Count == 0)
            {
                ViewModel.StatusText = "Load cancelled: audio file not selected";
                return;
            }

            // Повторная попытка с override: рекурсия останавливается,
            // потому что overrideAudioPath гарантированно существует.
            await LoadProjectWithAudioResolutionAsync(projectPath, files[0].Path.LocalPath);
        }
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save NeuroViz project",
            SuggestedFileName = "project",
            DefaultExtension = "nvproj",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("NeuroViz project") { Patterns = new[] { "*.nvproj" } },
            },
        });

        if (file?.Path.LocalPath is { } target)
            await ViewModel.SaveProjectAsync(target);
    }

    private void OnTimelineSeek(object? sender, double seconds) => ViewModel.SeekTo(seconds);

    private void OnSliderDragStarted(object? sender, VectorEventArgs e)
    {
        // Сообщаем VM, что пользователь взял ползунок,
        // чтобы таймер перестал перезаписывать SliderPosition
        if (DataContext is MainWindowViewModel vm)
            vm.IsDragging = true;
    }

    private void OnSliderDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsDragging = false;
            // Выполняем перемотку на новую позицию
            vm.SeekTo(PositionSlider.Value);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel.Dispose();
    }
}
