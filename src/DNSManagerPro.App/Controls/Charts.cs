using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DNSManagerPro.App.Controls;

/// <summary>Panel that lays children out in equal-width columns, choosing the column count from the available width.</summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(340.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (cols, colWidth) = Layout(availableSize.Width);
        var rowHeights = new List<double>();
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            var row = i / cols;
            if (row >= rowHeights.Count)
            {
                rowHeights.Add(0);
            }

            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
            i++;
        }

        var width = double.IsInfinity(availableSize.Width) ? colWidth * cols : availableSize.Width;
        return new Size(width, rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (cols, colWidth) = Layout(finalSize.Width);
        var heights = new List<double>();
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            var row = i / cols;
            if (row >= heights.Count)
            {
                heights.Add(0);
            }

            heights[row] = Math.Max(heights[row], child.DesiredSize.Height);
            i++;
        }

        i = 0;
        foreach (UIElement child in InternalChildren)
        {
            var row = i / cols;
            var col = i % cols;
            var y = heights.Take(row).Sum();
            child.Arrange(new Rect(col * colWidth, y, colWidth, heights[row]));
            i++;
        }

        return finalSize;
    }

    private (int Cols, double ColWidth) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0)
        {
            return (1, MinColumnWidth);
        }

        var cols = Math.Clamp((int)(width / MinColumnWidth), 1, Math.Max(1, InternalChildren.Count));
        return (cols, width / cols);
    }
}

/// <summary>Lightweight line/area chart for a series of latency samples. NaN marks a failed sample and breaks the line.</summary>
public sealed class SparklineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(SparklineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (SparklineChart)d;
        if (e.OldValue is INotifyCollectionChanged oldC)
        {
            oldC.CollectionChanged -= chart.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newC)
        {
            newC.CollectionChanged += chart.OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private List<double> Samples() => (Values ?? Array.Empty<double>()).OfType<double>().ToList();

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var s = Samples();
        if (s.Count == 0 || ActualWidth <= 0)
        {
            ToolTip = null;
            return;
        }

        var x = e.GetPosition(this).X;
        var idx = s.Count == 1 ? 0 : (int)Math.Round(x / ActualWidth * (s.Count - 1));
        idx = Math.Clamp(idx, 0, s.Count - 1);
        ToolTip = double.IsNaN(s[idx]) ? $"Sample {idx + 1}: failed / timeout" : $"Sample {idx + 1}: {s[idx]:F0} ms";
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var s = Samples();
        const double pad = 6;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1);
        for (var i = 0; i < 4; i++)
        {
            var y = pad + ((h - (pad * 2)) * i / 3);
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var valid = s.Where(v => !double.IsNaN(v)).ToList();
        if (valid.Count == 0)
        {
            return;
        }

        var max = Math.Max(valid.Max() * 1.15, 10);
        double X(int i) => s.Count == 1 ? w / 2 : i * w / (s.Count - 1);
        double Y(double v) => h - pad - (v / max * (h - (pad * 2)));

        var pen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round };
        var fill = new LinearGradientBrush(
            Color.FromArgb(70, ((SolidColorBrush)LineBrush).Color.R, ((SolidColorBrush)LineBrush).Color.G, ((SolidColorBrush)LineBrush).Color.B),
            Colors.Transparent,
            90);
        var fail = new Pen(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2);

        var run = new List<Point>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(run[0].X, h - pad), true, true);
                    foreach (var p in run)
                    {
                        g.LineTo(p, true, false);
                    }

                    g.LineTo(new Point(run[^1].X, h - pad), true, false);
                }

                area.Freeze();
                dc.DrawGeometry(fill, null, area);
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(run[0], false, false);
                    g.PolyLineTo(run.Skip(1).ToList(), true, true);
                }

                line.Freeze();
                dc.DrawGeometry(null, pen, line);
            }
            else if (run.Count == 1)
            {
                dc.DrawEllipse(LineBrush, null, run[0], 3, 3);
            }

            run.Clear();
        }

        for (var i = 0; i < s.Count; i++)
        {
            if (double.IsNaN(s[i]))
            {
                Flush();
                dc.DrawLine(fail, new Point(X(i), pad), new Point(X(i), h - pad));
            }
            else
            {
                run.Add(new Point(X(i), Y(s[i])));
            }
        }

        Flush();
        var last = s.Count - 1;
        if (!double.IsNaN(s[last]))
        {
            dc.DrawEllipse(LineBrush, new Pen(Brushes.White, 1.5), new Point(X(last), Y(s[last])), 4, 4);
        }
    }
}



/// <summary>A named line in <see cref="MultiLineChart"/>. NaN values leave gaps.</summary>
public sealed record ChartSeries(string Name, Brush Brush, IReadOnlyList<double> Values);

/// <summary>Horizontal progress-style bar: fills Fraction (0..1) of its width.</summary>
public sealed class FillBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(FillBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FillBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var h = Math.Min(ActualHeight, 12);
        var y = (ActualHeight - h) / 2;
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), null, new Rect(0, y, ActualWidth, h), h / 2, h / 2);
        var w = Math.Clamp(Fraction, 0, 1) * ActualWidth;
        if (w > 0)
        {
            drawingContext.DrawRoundedRectangle(Fill, null, new Rect(0, y, Math.Max(w, h), h), h / 2, h / 2);
        }
    }
}

/// <summary>Min-to-max span on a 0..AxisMax axis with median tick and mean dot.</summary>
public sealed class RangeBar : FrameworkElement
{
    private static DependencyProperty Reg(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(RangeBar), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinProperty = Reg(nameof(Min), 0);
    public static readonly DependencyProperty MaxProperty = Reg(nameof(Max), 0);
    public static readonly DependencyProperty MedianProperty = Reg(nameof(Median), 0);
    public static readonly DependencyProperty MeanProperty = Reg(nameof(Mean), 0);
    public static readonly DependencyProperty AxisMaxProperty = Reg(nameof(AxisMax), 1);

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(RangeBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Min { get => (double)GetValue(MinProperty); set => SetValue(MinProperty, value); }

    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

    public double Median { get => (double)GetValue(MedianProperty); set => SetValue(MedianProperty, value); }

    public double Mean { get => (double)GetValue(MeanProperty); set => SetValue(MeanProperty, value); }

    public double AxisMax { get => (double)GetValue(AxisMaxProperty); set => SetValue(AxisMaxProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var axis = AxisMax <= 0 ? 1 : AxisMax;
        double X(double v) => Math.Clamp(v / axis, 0, 1) * ActualWidth;
        var h = Math.Min(ActualHeight, 14);
        var y = (ActualHeight - h) / 2;
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 148, 163, 184)), null, new Rect(0, y, ActualWidth, h), 4, 4);
        var x0 = X(Min);
        var x1 = Math.Max(X(Max), x0 + 3);
        var span = Fill.Clone();
        span.Opacity = 0.45;
        drawingContext.DrawRoundedRectangle(span, null, new Rect(x0, y, x1 - x0, h), 4, 4);
        drawingContext.DrawLine(new Pen(Brushes.White, 2), new Point(X(Median), y - 2), new Point(X(Median), y + h + 2));
        drawingContext.DrawEllipse(Fill, new Pen(Brushes.White, 1), new Point(X(Mean), y + (h / 2)), 3.5, 3.5);
    }
}

/// <summary>Multi-series line chart (one point per benchmark run) with hover details.</summary>
public sealed class MultiLineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(MultiLineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (MultiLineChart)d;
        if (e.OldValue is INotifyCollectionChanged oldC)
        {
            oldC.CollectionChanged -= chart.OnSeriesCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newC)
        {
            newC.CollectionChanged += chart.OnSeriesCollectionChanged;
        }
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public IEnumerable<ChartSeries>? Series
    {
        get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    private const double Left = 38;
    private const double Pad = 8;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var list = (Series ?? []).ToList();
        var n = list.Count == 0 ? 0 : list.Max(s => s.Values.Count);
        if (n == 0)
        {
            ToolTip = null;
            return;
        }

        var x = e.GetPosition(this).X;
        var idx = n == 1 ? 0 : (int)Math.Round((x - Left) / Math.Max(1, ActualWidth - Left - Pad) * (n - 1));
        idx = Math.Clamp(idx, 0, n - 1);
        var lines = list.Where(s => idx < s.Values.Count && !double.IsNaN(s.Values[idx]))
            .Select(s => $"{s.Name}: {s.Values[idx]:F1} ms");
        ToolTip = $"Run {idx + 1} of {n}\n" + string.Join("\n", lines);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var list = (Series ?? []).ToList();
        var all = list.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).ToList();
        var w = ActualWidth - Left - Pad;
        var h = ActualHeight - (Pad * 2);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var max = all.Count == 0 ? 10 : Math.Max(all.Max() * 1.15, 10);

        for (var i = 0; i <= 3; i++)
        {
            var y = Pad + (h * i / 3);
            dc.DrawLine(grid, new Point(Left, y), new Point(ActualWidth - Pad, y));
            var label = new FormattedText(
                $"{max * (3 - i) / 3:F0}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10.5, new SolidColorBrush(Color.FromRgb(148, 163, 184)), dpi);
            dc.DrawText(label, new Point(Left - label.Width - 6, y - (label.Height / 2)));
        }

        var n = list.Count == 0 ? 0 : list.Max(s => s.Values.Count);
        if (n == 0)
        {
            return;
        }

        double X(int i) => Left + (n == 1 ? w / 2 : i * w / (n - 1));
        double Y(double v) => Pad + h - (v / max * h);
        foreach (var s in list)
        {
            var pen = new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round };
            Point? prev = null;
            for (var i = 0; i < s.Values.Count; i++)
            {
                if (double.IsNaN(s.Values[i]))
                {
                    prev = null;
                    continue;
                }

                var p = new Point(X(i), Y(s.Values[i]));
                if (prev is { } q)
                {
                    dc.DrawLine(pen, q, p);
                }

                dc.DrawEllipse(s.Brush, null, p, 3, 3);
                prev = p;
            }
        }
    }
}

