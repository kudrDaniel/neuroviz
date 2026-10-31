using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NeuroViz.App.Controls;

public sealed class SpectrumControl : Control
{
    public static readonly StyledProperty<float[]?> ValuesProperty =
        AvaloniaProperty.Register<SpectrumControl, float[]?>(nameof(Values));

    public float[]? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    static SpectrumControl()
    {
        AffectsRender<SpectrumControl>(ValuesProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Values == null || Values.Length == 0) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var barWidth = w / Values.Length;

        float max = 0.001f;
        foreach (var v in Values) if (v > max) max = v;

        var brush = new SolidColorBrush(Color.FromRgb(100, 200, 255));

        for (int i = 0; i < Values.Length; i++)
        {
            var val = Values[i] / max;
            var barHeight = val * h;
            var rect = new Rect(i * barWidth, h - barHeight, Math.Max(1, barWidth - 1), barHeight);
            ctx.FillRectangle(brush, rect);
        }
    }
}