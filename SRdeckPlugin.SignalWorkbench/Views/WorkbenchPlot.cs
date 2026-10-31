using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SRdeckPlugin.SignalWorkbench.Views;

public enum WorkbenchPlotKind { Constellation, FrequencyHistogram, FrequencyTrace }

public sealed class WorkbenchPlot : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(SignalSnapshot), typeof(WorkbenchPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind),
        typeof(WorkbenchPlotKind), typeof(WorkbenchPlot), new FrameworkPropertyMetadata(WorkbenchPlotKind.Constellation, FrameworkPropertyMetadataOptions.AffectsRender));

    public SignalSnapshot? Snapshot { get => (SignalSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public WorkbenchPlotKind Kind { get => (WorkbenchPlotKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 80 || ActualHeight < 60) return;
        Brush background = Brush("InstrumentBaseBrush", Brushes.Black);
        Brush grid = Brush("TextDimBrush", Brushes.Gray);
        Brush accent = Brush("PluginDisplayAccentPrimaryBrush", Brushes.Cyan);
        dc.DrawRectangle(background, null, new(0, 0, ActualWidth, ActualHeight));
        var pen = new Pen(grid, 0.5);
        var area = new Rect(36, 18, ActualWidth - 48, ActualHeight - 44);
        if (Kind == WorkbenchPlotKind.FrequencyHistogram)
        {
            dc.DrawLine(pen, area.BottomLeft, area.BottomRight);
            dc.DrawLine(pen, area.TopLeft, area.BottomLeft);
            dc.DrawLine(pen, new(area.Left + area.Width / 2, area.Top), new(area.Left + area.Width / 2, area.Bottom));
            if (Snapshot is { } snapshot)
            {
                int[] bins = snapshot.FrequencyHistogram;
                int peak = Math.Max(1, bins.Max());
                for (int k = 0; k < bins.Length; k++)
                {
                    double height = bins[k] * area.Height / peak;
                    dc.DrawRectangle(accent, null, new Rect(area.Left + k * area.Width / bins.Length,
                        area.Bottom - height, Math.Max(1, area.Width / bins.Length - 0.5), height));
                }
                Label(dc, $"{peak:N0}", 2, area.Top, grid);
                Label(dc, $"−{snapshot.HistogramHalfRangeHz:N0}", area.Left, area.Bottom + 5, grid);
                Label(dc, "0 Hz", area.Left + area.Width / 2 - 12, area.Bottom + 5, grid);
                Label(dc, $"+{snapshot.HistogramHalfRangeHz:N0}", area.Right, area.Bottom + 5, grid, true);
                foreach (FskTone tone in snapshot.Fsk?.Tones ?? [])
                {
                    double x = area.Left + (tone.FrequencyHz + snapshot.HistogramHalfRangeHz) /
                        (2 * snapshot.HistogramHalfRangeHz) * area.Width;
                    if (x >= area.Left && x <= area.Right)
                        dc.DrawLine(new Pen(Brush("PluginDisplayAccentSecondaryBrush", Brushes.Orange), 1),
                            new(x, area.Top), new(x, area.Bottom));
                }
            }
        }
        else if (Kind == WorkbenchPlotKind.FrequencyTrace)
        {
            dc.DrawLine(pen, area.BottomLeft, area.BottomRight);
            dc.DrawLine(pen, new(area.Left, area.Top + area.Height / 2), new(area.Right, area.Top + area.Height / 2));
            if (Snapshot is { FrequencyTraceHz.Length: > 1 } snapshot)
            {
                double range = snapshot.HistogramHalfRangeHz;
                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    bool drawing = false;
                    for (int n = 0; n < snapshot.FrequencyTraceHz.Length; n++)
                    {
                        float frequency = snapshot.FrequencyTraceHz[n];
                        if (!float.IsFinite(frequency)) { drawing = false; continue; }
                        Point point = new(area.Left + n * area.Width / (snapshot.FrequencyTraceHz.Length - 1),
                            area.Top + area.Height * (1 - frequency / range) / 2);
                        if (!drawing) context.BeginFigure(point, false, false);
                        else context.LineTo(point, true, false);
                        drawing = true;
                    }
                }
                dc.PushClip(new RectangleGeometry(area));
                dc.DrawGeometry(null, new Pen(accent, 1), geometry);
                dc.Pop();
                Label(dc, $"±{range:N0} Hz", area.Left + 4, area.Top, grid);
                Label(dc, "0 ms", area.Left, area.Bottom + 5, grid);
                Label(dc, $"{(snapshot.FrequencyTraceHz.Length - 1) * 1_000.0 / snapshot.SampleRateHz:F2} ms",
                    area.Right, area.Bottom + 5, grid, true);
            }
        }
        else
        {
            double size = Math.Min(area.Width, area.Height);
            area = new Rect(area.Left + (area.Width - size) / 2, area.Top, size, size);
            Point center = new(area.Left + area.Width / 2, area.Top + area.Height / 2);
            double scale = Math.Min(area.Width, area.Height) / 5;
            dc.DrawLine(pen, new(area.Left, center.Y), new(area.Right, center.Y));
            dc.DrawLine(pen, new(center.X, area.Top), new(center.X, area.Bottom));
            dc.DrawEllipse(null, pen, center, scale, scale);
            Label(dc, "Q", center.X + 5, area.Top, grid);
            Label(dc, "I", area.Right - 10, center.Y + 5, grid);
            dc.PushClip(new RectangleGeometry(area));
            foreach (var point in Snapshot?.Constellation ?? [])
                if (float.IsFinite(point.I) && float.IsFinite(point.Q))
                    dc.DrawEllipse(accent, null, new(center.X + point.I * scale, center.Y - point.Q * scale), 1.3, 1.3);
            dc.Pop();
        }
        if (Snapshot is null) Label(dc, "解析データ待ち", area.Left + 12, area.Top + 12, grid);
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void Label(DrawingContext dc, string text, double x, double y, Brush brush, bool right = false)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(right ? x - label.Width : x, y));
    }
}
