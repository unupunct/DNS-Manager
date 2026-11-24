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


