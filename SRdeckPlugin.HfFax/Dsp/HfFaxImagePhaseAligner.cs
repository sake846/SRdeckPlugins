using SRdeckPlugin.HfFax.Models;

namespace SRdeckPlugin.HfFax.Dsp;

internal static class HfFaxImagePhaseAligner
{
    public const int MinimumLines = 64;

    private const double MarkerWindowRatio = 0.03;
    private const double MarkerTargetCenterRatio = 0.025;
    private const int MaximumSampledLines = 512;
    private const byte DarkPixelThreshold = 80;
    private const double MaximumMarkerMean = 150;
    private const double MinimumDarkFraction = 0.20;
    private const double MinimumMarkerContrast = 18;

    public static bool TryGetCorrection(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int manualShiftPixels,
        out int correctionPixels)
    {
        correctionPixels = 0;
        if (width < 100 || height < MinimumLines) return false;
        int requiredLength;
        try { requiredLength = checked(width * height); }
        catch (OverflowException) { return false; }
        if (pixels.Length < requiredLength) return false;

        int window = Math.Clamp(
            (int)Math.Round(width * MarkerWindowRatio), 12, Math.Max(12, width / 8));
        int rowStep = Math.Max(1, height / MaximumSampledLines);
        var columnSums = new long[width];
        var columnDarkCounts = new int[width];
        int sampledLines = 0;
        for (int row = rowStep / 2; row < height; row += rowStep)
        {
            int rowOffset = row * width;
            for (int column = 0; column < width; column++)
            {
                byte value = pixels[rowOffset + column];
                columnSums[column] += value;
                if (value < DarkPixelThreshold) columnDarkCounts[column]++;
            }
            sampledLines++;
        }
        if (sampledLines == 0) return false;

        long brightnessSum = 0;
        int darkCount = 0;
        for (int column = 0; column < window; column++)
        {
            brightnessSum += columnSums[column];
            darkCount += columnDarkCounts[column];
        }

        int bestStart = 0;
        long bestBrightnessSum = brightnessSum;
        int bestDarkCount = darkCount;
        var windowMeans = new double[width];
        windowMeans[0] = brightnessSum / (double)(sampledLines * window);
        for (int start = 1; start < width; start++)
        {
            int outgoing = start - 1;
            int incoming = (start + window - 1) % width;
            brightnessSum += columnSums[incoming] - columnSums[outgoing];
            darkCount += columnDarkCounts[incoming] - columnDarkCounts[outgoing];
            windowMeans[start] = brightnessSum / (double)(sampledLines * window);
            if (brightnessSum < bestBrightnessSum)
            {
                bestStart = start;
                bestBrightnessSum = brightnessSum;
                bestDarkCount = darkCount;
            }
        }

        double bestMean = bestBrightnessSum / (double)(sampledLines * window);
        double bestDarkFraction = bestDarkCount / (double)(sampledLines * window);
        Array.Sort(windowMeans);
        double medianMean = windowMeans[windowMeans.Length / 2];
        if (bestMean > MaximumMarkerMean ||
            bestDarkFraction < MinimumDarkFraction ||
            medianMean - bestMean < MinimumMarkerContrast)
            return false;

        int markerCenter = (bestStart + window / 2) % width;
        int targetCenter = NormalizeShift(
            (int)Math.Round(width * MarkerTargetCenterRatio) + manualShiftPixels, width);
        correctionPixels = NormalizeShift(targetCenter - markerCenter, width);
        return true;
    }

    public static HfFaxImageSnapshot AlignSnapshot(
        HfFaxImageSnapshot image,
        int manualShiftPixels = 0)
    {
        if (!TryGetCorrection(image.Pixels, image.Width, image.Height,
                manualShiftPixels, out int correction) || correction == 0)
            return image;

        byte[] aligned = image.Pixels.ToArray();
        RotateRows(aligned, image.Width, image.Height, correction);
        int combinedShift = NormalizeShift(
            image.AutomaticHorizontalShiftPixels + correction, image.Width);
        return image with
        {
            Pixels = aligned,
            AutomaticHorizontalShiftPixels = SignedShift(combinedShift, image.Width)
        };
    }

    public static void RotateRows(Span<byte> pixels, int width, int height, int shift)
    {
        if (width <= 0 || height <= 0 || pixels.Length < checked(width * height)) return;
        int normalized = NormalizeShift(shift, width);
        if (normalized == 0) return;
        for (int row = 0; row < height; row++)
            HfFaxReceiver.RotateLine(pixels.Slice(row * width, width), normalized);
    }

    public static int SignedShift(int normalizedShift, int width)
    {
        int normalized = NormalizeShift(normalizedShift, width);
        return normalized > width / 2 ? normalized - width : normalized;
    }

    private static int NormalizeShift(int shift, int width)
    {
        if (width <= 0) return 0;
        int normalized = shift % width;
        return normalized < 0 ? normalized + width : normalized;
    }
}
