using System.Collections.Specialized;
using System.Collections.Generic;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NeuroViz.App.ViewModels;

namespace NeuroViz.App.Controls;

public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<IEnumerable<EventRow>?> EventsProperty =
        AvaloniaProperty.Register<TimelineControl, IEnumerable<EventRow>?>(nameof(Events));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Duration));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Position));

    public static readonly StyledProperty<IBrush> EventBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush>(
            nameof(EventBrush), Brushes.DeepSkyBlue);

    public static readonly StyledProperty<IBrush> PlayheadBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush>(
            nameof(PlayheadBrush), Brushes.OrangeRed);

    public IEnumerable<EventRow>? Events
    {
        get => GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IBrush EventBrush
    {
        get => GetValue(EventBrushProperty);
        set => SetValue(EventBrushProperty, value);
    }

    public IBrush PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public event EventHandler<double>? SeekRequested;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(
            EventsProperty, DurationProperty, PositionProperty,
            EventBrushProperty, PlayheadBrushProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Фон
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 26)), new Rect(0, 0, w, h));

        var duration = Duration;
        if (duration <= 0) return;

        var eventPen = new Pen(EventBrush, 1);
        var scale = w / duration;

        if (Events is not null)
        {
            foreach (var row in Events)
            {
                var x = row.Timestamp * scale;
                if (x < 0 || x > w) continue;

                var alpha = (byte)Math.Clamp(80 + 175 * row.Confidence, 80, 255);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, 135, 206, 250));
                var pen = new Pen(brush, 1);
                ctx.DrawLine(pen, new Point(x, h * 0.15), new Point(x, h * 0.85));
            }
        }

        // Playhead
        var px = Math.Clamp(Position * scale, 0, w);
        var playheadPen = new Pen(PlayheadBrush, 2);
        ctx.DrawLine(playheadPen, new Point(px, 0), new Point(px, h));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EventsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldObs)
                oldObs.CollectionChanged -= OnCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newObs)
                newObs.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var w = Bounds.Width;
        if (w <= 0 || Duration <= 0) return;

        var frac = Math.Clamp(point.X / w, 0.0, 1.0);
        SeekRequested?.Invoke(this, frac * Duration);
        e.Handled = true;
    }
}