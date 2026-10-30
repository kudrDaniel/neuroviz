using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using NeuroViz.App.ViewModels;

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