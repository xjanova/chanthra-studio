using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChanthraStudio.Controls;

/// <summary>
/// Tiny scrolling line+fill chart for status-bar-sized GPU telemetry.
/// Set <see cref="Values"/> to redraw — values are auto-normalized across
/// the visible range and stretched to fill the control's actual size.
/// </summary>
public partial class Sparkline : UserControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable<double>),
        typeof(Sparkline),
        new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue),
        typeof(double),
        typeof(Sparkline),
        new PropertyMetadata(100.0, (d, _) => ((Sparkline)d).Render()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is Sparkline s && e.NewValue is Brush b) s.Line.Stroke = b;
        }));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Upper bound for normalization (e.g. 100 for percent, GPU temp 100°C).</summary>
    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Sparkline()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Sparkline s) s.Render();
    }

    private void Render()
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        var arr = (Values ?? Array.Empty<double>()).ToArray();
        if (arr.Length < 2)
        {
            Line.Points = new PointCollection();
            Fill.Points = new PointCollection();
            return;
        }

        var max = MaxValue > 0 ? MaxValue : Math.Max(1, arr.Max());
        var stepX = w / Math.Max(1, arr.Length - 1);
        var line = new PointCollection();
        var fill = new PointCollection { new Point(0, h) };

        for (int i = 0; i < arr.Length; i++)
        {
            var x = i * stepX;
            var clamped = Math.Clamp(arr[i] / max, 0, 1);
            // Reserve 1.5px top/bottom so the stroke doesn't clip
            var y = h - 1.5 - (clamped * (h - 3));
            var p = new Point(x, y);
            line.Add(p);
            fill.Add(p);
        }
        fill.Add(new Point(w, h));

        Line.Points = line;
        Fill.Points = fill;
    }
}
