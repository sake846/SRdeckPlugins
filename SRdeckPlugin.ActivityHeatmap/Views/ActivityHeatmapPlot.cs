using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SRdeckPlugin.ActivityHeatmap.Views;

public sealed class ActivityHeatmapPlot : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(ActivitySnapshot), typeof(ActivityHeatmapPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SnapshotChanged));
    private BitmapSource? bitmap;
    private string cellDescription = string.Empty;
    private int selectedColumn = ActivityAnalyzer.Columns / 2, selectedRow;
    private int Rows => Snapshot?.RowTimes.Length ?? 360;
    private Rect Area => new(80, 12, Math.Max(0, ActualWidth - 96), Math.Max(0, ActualHeight - 80));
    public ActivitySnapshot? Snapshot { get => (ActivitySnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    private static void SnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var plot = (ActivityHeatmapPlot)sender;
        plot.bitmap = null;
        if (plot.Snapshot is not { } snapshot)
        {
            plot.ToolTip = null;
            plot.cellDescription = string.Empty;
            AutomationProperties.SetItemStatus(plot, string.Empty);
            return;
        }
        int rows = snapshot.RowTimes.Length;
        plot.selectedRow = Math.Min(plot.selectedRow, rows - 1);
        var pixels = new byte[ActivityAnalyzer.Columns * rows * 4];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < ActivityAnalyzer.Columns; column++)
            {
                float value = snapshot.Activity[row * ActivityAnalyzer.Columns + column];
                Color color = ActivityColor(value);
                int offset = (row * ActivityAnalyzer.Columns + column) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        plot.bitmap = BitmapSource.Create(ActivityAnalyzer.Columns, rows,
            96, 96, PixelFormats.Bgra32, null, pixels, ActivityAnalyzer.Columns * 4);
        plot.bitmap.Freeze();
        plot.DescribeCell();
    }

    private static Color ActivityColor(float value)
    {
        if (!float.IsFinite(value)) return Color.FromRgb(40, 40, 40);
        value = Math.Clamp(value, 0, 1);
        return value <= 0.5f
            ? Color.FromRgb((byte)(16 + value * 16), (byte)(35 + value * 330), (byte)(57 + value * 270))
            : Color.FromRgb((byte)(24 + (value - 0.5f) * 462), (byte)(200 + (value - 0.5f) * 110),
                (byte)(192 - (value - 0.5f) * 304));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 160 || ActualHeight < 120) return;
        Brush text = TryFindResource("TextSecondaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        Brush background = TryFindResource("InstrumentBaseBrush") as Brush ?? SystemColors.ControlBrush;
        dc.DrawRectangle(background, null, new(0, 0, ActualWidth, ActualHeight));
        Rect area = Area;
        if (bitmap is not null) dc.DrawImage(bitmap, area);
        if (Snapshot is { } snapshot)
        {
            for (int i = 0; i <= 4; i++)
            {
                double frequency = snapshot.LowerFrequencyHz + (snapshot.UpperFrequencyHz - snapshot.LowerFrequencyHz) * i / 4.0;
                Label(dc, $"{frequency / 1e6:F4}", area.Left + area.Width * i / 4, area.Bottom + 4, text, i / 4.0);
                int row = i * (Rows - 1) / 4;
                string format = snapshot.RowDurationSeconds == 240 ? "MM/dd HH:mm" : "HH:mm";
                Label(dc, snapshot.RowTimes[row].ToLocalTime().ToString(format),
                    area.Left - 6, area.Top + (area.Height - 12) * i / 4, text, 1);
            }
        }
        else Label(dc, "受信データ待ち", area.Left + 8, area.Top + 8, text);
        Label(dc, "MHz", area.Right, area.Bottom + 20, text, 1);
        for (int i = 0; i <= 4; i++)
        {
            double x = area.Left + i * 76;
            dc.DrawRectangle(new SolidColorBrush(ActivityColor(i / 4f)), null, new(x, area.Bottom + 40, 14, 10));
            Label(dc, $"{i * 25}%", x + 18, area.Bottom + 38, text);
        }
        dc.DrawRectangle(new SolidColorBrush(ActivityColor(float.NaN)), null, new(area.Right - 70, area.Bottom + 40, 14, 10));
        Label(dc, "未観測", area.Right - 52, area.Bottom + 38, text);
        Label(dc, cellDescription, area.Left, area.Bottom + 58, text);
        if (IsKeyboardFocused)
        {
            double x = area.Left + selectedColumn * area.Width / ActivityAnalyzer.Columns;
            double y = area.Top + selectedRow * area.Height / Rows;
            dc.DrawRectangle(null, new Pen(text, 1), new(x, y, Math.Max(2, area.Width / ActivityAnalyzer.Columns),
                Math.Max(2, area.Height / Rows)));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (!Area.Contains(point)) { ToolTip = null; return; }
        selectedColumn = Math.Clamp((int)((point.X - Area.Left) / Area.Width * ActivityAnalyzer.Columns), 0, ActivityAnalyzer.Columns - 1);
        selectedRow = Math.Clamp((int)((point.Y - Area.Top) / Area.Height * Rows), 0, Rows - 1);
        DescribeCell();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: selectedColumn = Math.Max(0, selectedColumn - 1); break;
            case Key.Right: selectedColumn = Math.Min(ActivityAnalyzer.Columns - 1, selectedColumn + 1); break;
            case Key.Up: selectedRow = Math.Max(0, selectedRow - 1); break;
            case Key.Down: selectedRow = Math.Min(Rows - 1, selectedRow + 1); break;
            default: return;
        }
        DescribeCell();
        e.Handled = true;
        InvalidateVisual();
    }

    private void DescribeCell()
    {
        if (Snapshot is not { } snapshot) return;
        int row = selectedRow;
        double lower = snapshot.LowerFrequencyHz + selectedColumn * snapshot.CellWidthHz;
        float value = snapshot.Activity[row * ActivityAnalyzer.Columns + selectedColumn];
        string activity = float.IsFinite(value) ? $"活動率 {value:P0} / {snapshot.CellObservations[row * ActivityAnalyzer.Columns + selectedColumn]}回観測" : "未観測";
        string description = $"{snapshot.RowTimes[row].ToLocalTime():MM/dd HH:mm}〜（{snapshot.RowDurationSeconds / 60}分）\n" +
            $"{lower:N0}〜{lower + snapshot.CellWidthHz:N0} Hz\n{activity} / 判定 {snapshot.ThresholdDbm} dBm";
        ToolTip = description;
        cellDescription = description.Replace('\n', ' ');
        AutomationProperties.SetItemStatus(this, description);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Label(DrawingContext dc, string value, double x, double y, Brush brush, double alignment = 0)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new(x - text.Width * alignment, y));
    }
}
