using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SRdeckPlugin.Aprs.Models;

namespace SRdeckPlugin.Aprs.Views;

public sealed class AprsTimeSeriesPlot : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points),
        typeof(IReadOnlyList<AprsGraphPoint>), typeof(AprsTimeSeriesPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ResetSelection));
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric),
        typeof(AprsMeasurement), typeof(AprsTimeSeriesPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ResetSelection));
    public IReadOnlyList<AprsGraphPoint>? Points { get => (IReadOnlyList<AprsGraphPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public AprsMeasurement? Metric { get => (AprsMeasurement?)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    private int selectedIndex;
    private Rect Area => new(86, 30, Math.Max(1, ActualWidth - 108), Math.Max(1, ActualHeight - 68));

    private static void ResetSelection(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var plot = (AprsTimeSeriesPlot)sender;
        plot.selectedIndex = Math.Max(0, (plot.Points?.Count ?? 0) - 1);
        plot.DescribePoint();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 220 || ActualHeight < 100) return;
        Brush text = TryFindResource("TextSecondaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        Brush series = TryFindResource("PluginDataSeries1Brush") as Brush ?? SystemColors.HighlightBrush;
        Brush grid = TryFindResource("ControlBorderBrush") as Brush ?? SystemColors.ControlDarkBrush;
        dc.DrawRectangle(TryFindResource("InstrumentBaseBrush") as Brush ?? SystemColors.WindowBrush,
            null, new(0, 0, ActualWidth, ActualHeight));
        Rect area = Area;
        Label(Metric?.Label ?? "数値データ待ち", area.Left, 6);
        if (Points is not { Count: > 0 } points) return;
        double low = points.Min(p => p.Value), high = points.Max(p => p.Value);
        if (Metric?.Unit == "bit") { low = 0; high = 1; }
        else if (low == high) { double padding = Math.Max(1, Math.Abs(low) * .05); low -= padding; high += padding; }
        double span = (points[^1].ReceivedAt - points[0].ReceivedAt).TotalSeconds;
        double yRange = high - low;
        double Y(double value) => area.Bottom - area.Height * (value - low) / yRange;
        double X(AprsGraphPoint point) => span == 0 ? area.Left + area.Width / 2
            : area.Left + area.Width * (point.ReceivedAt - points[0].ReceivedAt).TotalSeconds / span;
        for (int i = 0; i <= 4; i++)
        {
            double y = area.Bottom - area.Height * i / 4;
            dc.DrawLine(new(grid, .5), new(area.Left, y), new(area.Right, y));
            double value = low * (1 - i / 4.0) + high * (i / 4.0);
            Label(value.ToString("G5", CultureInfo.CurrentCulture), area.Left - 8, y - 7, 1);
        }
        dc.DrawLine(new(text, 1), area.BottomLeft, area.BottomRight);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(new(X(points[0]), Y(points[0].Value)), false, false);
            for (int i = 1; i < points.Count; i++)
            {
                if (Metric?.Unit == "bit") path.LineTo(new(X(points[i]), Y(points[i - 1].Value)), true, false);
                path.LineTo(new(X(points[i]), Y(points[i].Value)), true, false);
            }
        }
        geometry.Freeze();
        dc.PushClip(new RectangleGeometry(new(area.Left - 4, area.Top - 4, area.Width + 8, area.Height + 8)));
        dc.DrawGeometry(null, new(series, 1.5), geometry);
        foreach (var point in points) dc.DrawEllipse(series, null, new(X(point), Y(point.Value)), 2, 2);
        selectedIndex = Math.Clamp(selectedIndex, 0, points.Count - 1);
        if (IsKeyboardFocusWithin || IsMouseOver)
            dc.DrawEllipse(null, new(text, 1), new(X(points[selectedIndex]), Y(points[selectedIndex].Value)), 5, 5);
        dc.Pop();
        string format = span >= 86400 ? "MM/dd HH:mm" : "HH:mm:ss";
        Label(points[0].ReceivedAt.ToLocalTime().ToString(format), area.Left, area.Bottom + 6);
        Label(points[^1].ReceivedAt.ToLocalTime().ToString(format), area.Right, area.Bottom + 6, 1);

        void Label(string value, double x, double y, double anchor = 0)
        {
            var label = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new(x - label.Width * anchor, y));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Points is not { Count: > 0 } points) return;
        double fraction = Math.Clamp((e.GetPosition(this).X - Area.Left) / Area.Width, 0, 1);
        double seconds = (points[^1].ReceivedAt - points[0].ReceivedAt).TotalSeconds * fraction;
        selectedIndex = Enumerable.Range(0, points.Count)
            .MinBy(i => Math.Abs((points[i].ReceivedAt - points[0].ReceivedAt).TotalSeconds - seconds));
        DescribePoint(); InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Points is not { Count: > 0 } points) return;
        switch (e.Key)
        {
            case Key.Left: selectedIndex--; break;
            case Key.Right: selectedIndex++; break;
            case Key.Home: selectedIndex = 0; break;
            case Key.End: selectedIndex = points.Count - 1; break;
            default: return;
        }
        selectedIndex = Math.Clamp(selectedIndex, 0, points.Count - 1);
        DescribePoint(); InvalidateVisual(); e.Handled = true;
    }

    private void DescribePoint()
    {
        string description = Points is { Count: > 0 } points
            ? $"{points[Math.Clamp(selectedIndex, 0, points.Count - 1)].ReceivedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss.fff} / " +
              $"{Metric?.Label}: {points[Math.Clamp(selectedIndex, 0, points.Count - 1)].Value:G8}"
            : "数値データ待ち";
        ToolTip = description;
        AutomationProperties.SetItemStatus(this, description);
    }
}
