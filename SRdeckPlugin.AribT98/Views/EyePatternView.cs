using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SRdeckPlugin.AribT98.Dsp;

namespace SRdeckPlugin.AribT98.Views;

public sealed class EyePatternView : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(float[]), typeof(EyePatternView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Samples { get => (float[]?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 40 || ActualHeight <= 40) return;

        var plot = new Rect(28, 10, ActualWidth - 38, ActualHeight - 34);
        var grid = new Pen(Brushes.DimGray, 1);
        double Y(double value) => plot.Top + plot.Height * (4 - value) / 8;
        void Label(string text, double x, double y) => drawingContext.DrawText(
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 10, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

        foreach (int level in new[] { -3, -1, 1, 3 })
        {
            drawingContext.DrawLine(grid, new(plot.Left, Y(level)), new(plot.Right, Y(level)));
            Label(level > 0 ? $"+{level}" : level.ToString(CultureInfo.InvariantCulture), 6, Y(level) - 6);
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + plot.Width * i / 4;
            drawingContext.DrawLine(grid, new(x, plot.Top), new(x, plot.Bottom));
            if (i % 2 == 0) Label(i == 0 ? "0" : i == 2 ? "T" : "2T", x - 5, plot.Bottom + 5);
        }

        var samples = Samples;
        int traceSamples = 2 * AribT98Receiver.SamplesPerSymbol + 1;
        if (samples is null || samples.Length < traceSamples)
        {
            Label("フレーム同期待ち", plot.Left + 8, plot.Top + plot.Height / 2);
            return;
        }

        var traces = new StreamGeometry();
        using (var context = traces.Open())
            for (int start = 0; start + traceSamples <= samples.Length; start += AribT98Receiver.SamplesPerSymbol)
            {
                bool connected = false;
                for (int i = 0; i < traceSamples; i++)
                {
                    float value = samples[start + i];
                    if (!float.IsFinite(value)) { connected = false; continue; }
                    var point = new Point(plot.Left + plot.Width * i / (traceSamples - 1), Y(value));
                    if (connected) context.LineTo(point, true, false);
                    else context.BeginFigure(point, false, false);
                    connected = true;
                }
            }
        drawingContext.PushClip(new RectangleGeometry(plot));
        drawingContext.DrawGeometry(null, new Pen(Brushes.LightGreen, 1), traces);
        drawingContext.Pop();
    }
}
