using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SRdeckPlugin.Analog.Views;

internal sealed class ThirdOctaveVfdSpectrum : FrameworkElement
{
    private const int BandCount = 25;
    private const int DotRows = 12;
    private static readonly Brush LitBrush = CreateBrush(Color.FromRgb(68, 246, 218));
    private static readonly Brush DimBrush = CreateBrush(Color.FromArgb(38, 68, 246, 218));
    private static readonly Brush LabelBrush = CreateBrush(Color.FromArgb(185, 68, 246, 218));
    private readonly float[] levelsDb = Enumerable.Repeat(-72f, BandCount).ToArray();

    public void SetLevels(ReadOnlySpan<float> values)
    {
        int count = Math.Min(values.Length, BandCount);
        Array.Fill(levelsDb, -72f);
        values[..count].CopyTo(levelsDb);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        const double labelHeight = 13;
        double graphHeight = Math.Max(0, ActualHeight - labelHeight);
        double slotWidth = ActualWidth / BandCount;
        double dotWidth = Math.Max(2, slotWidth - 3);
        double rowPitch = graphHeight / DotRows;
        double dotHeight = Math.Max(1.5, rowPitch - 2);

        for (int band = 0; band < BandCount; band++)
        {
            int litRows = (int)Math.Round(Math.Clamp((levelsDb[band] + 60f) / 60f, 0f, 1f) * DotRows);
            double x = (band * slotWidth) + ((slotWidth - dotWidth) / 2);
            for (int row = 0; row < DotRows; row++)
            {
                double y = graphHeight - ((row + 1) * rowPitch) + 1;
                drawingContext.DrawRoundedRectangle(
                    row < litRows ? LitBrush : DimBrush,
                    null,
                    new Rect(x, y, dotWidth, dotHeight),
                    1,
                    1);
            }
        }

        DrawLabel(drawingContext, "63", 0, graphHeight, slotWidth);
        DrawLabel(drawingContext, "250", 6, graphHeight, slotWidth);
        DrawLabel(drawingContext, "1k", 12, graphHeight, slotWidth);
        DrawLabel(drawingContext, "4k", 18, graphHeight, slotWidth);
        DrawLabel(drawingContext, "16k", 24, graphHeight, slotWidth);
    }

    private void DrawLabel(
        DrawingContext drawingContext,
        string text,
        int band,
        double y,
        double slotWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            8,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double center = (band + 0.5) * slotWidth;
        drawingContext.DrawText(formatted, new Point(center - (formatted.Width / 2), y));
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
