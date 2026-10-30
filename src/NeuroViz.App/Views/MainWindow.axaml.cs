using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NeuroViz.App.ViewModels;

namespace NeuroViz.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
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

    private void OnSliderPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        => ViewModel.IsDragging = true;

    private void OnSliderReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        ViewModel.IsDragging = false;
        ViewModel.SeekTo(PositionSlider.Value);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel.Dispose();
    }
}