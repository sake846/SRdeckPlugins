namespace SRdeckPlugin.SignalWorkbench;

public sealed record FskTone(double FrequencyHz, double Occupancy, double StandardDeviationHz);

public sealed record FskMeasurements(FskTone[] Tones, double CenterOffsetHz, double ToneSpacingHz)
{
    public double OuterDeviationHz => ToneSpacingHz * (Tones.Length - 1) / 2;
}

internal static class FskAnalyzer
{
    public static FskMeasurements? Analyze(IReadOnlyList<double> sortedFrequencies, double resolutionHz)
    {
        if (sortedFrequencies.Count < 512) return null;
        double low = sortedFrequencies[(int)((sortedFrequencies.Count - 1) * 0.01)];
        double high = sortedFrequencies[(int)((sortedFrequencies.Count - 1) * 0.99)];
        double span = high - low;
        if (span < resolutionHz * 8) return null;
        double origin = low - span * 0.1, binWidth = span * 1.2 / 256;
        var counts = new int[256];
        foreach (double frequency in sortedFrequencies)
        {
            int bin = (int)Math.Floor((frequency - origin) / binWidth);
            if (bin >= 0 && bin < counts.Length) counts[bin]++;
        }
        var smoothed = new double[counts.Length];
        for (int bin = 2; bin < counts.Length - 2; bin++)
            smoothed[bin] = (counts[bin - 2] + 2.0 * counts[bin - 1] + 3.0 * counts[bin] +
                2.0 * counts[bin + 1] + counts[bin + 2]) / 9;
        double maximum = smoothed.Max();
        var peaks = new List<int>();
        for (int bin = 3; bin < counts.Length - 3; bin++)
            if (smoothed[bin] >= maximum * 0.1 && smoothed[bin] > smoothed[bin - 1] &&
                smoothed[bin] >= smoothed[bin + 1]) peaks.Add(bin);

        // Merge ripples within one broad peak before counting distinct tone levels.
        for (int index = 0; index < peaks.Count - 1;)
        {
            int left = peaks[index], right = peaks[index + 1];
            double valley = double.MaxValue;
            for (int bin = left; bin <= right; bin++) valley = Math.Min(valley, smoothed[bin]);
            if (right - left < 4 || valley > Math.Min(smoothed[left], smoothed[right]) * 0.45)
            {
                peaks.RemoveAt(smoothed[left] < smoothed[right] ? index : index + 1);
                index = Math.Max(0, index - 1);
            }
            else index++;
        }
        if (peaks.Count is not (2 or 4 or 8)) return null;
        double[] centers = peaks.Select(bin => origin + (bin + 0.5) * binWidth).ToArray();
        double spacing = (centers[^1] - centers[0]) / (centers.Length - 1);
        if (spacing < resolutionHz * 4) return null;
        for (int index = 1; index < centers.Length; index++)
            if (Math.Abs(centers[index] - centers[index - 1] - spacing) > spacing * 0.2) return null;

        var tones = new FskTone[centers.Length];
        int assigned = 0;
        for (int level = 0; level < centers.Length; level++)
        {
            int count = 0;
            double sum = 0, squareSum = 0;
            foreach (double frequency in sortedFrequencies)
            {
                double offset = frequency - centers[level];
                if (Math.Abs(offset) > spacing * 0.25) continue;
                count++;
                sum += offset;
                squareSum += offset * offset;
            }
            if (count < sortedFrequencies.Count * 0.04) return null;
            double mean = sum / count;
            double deviation = Math.Sqrt(Math.Max(0, squareSum / count - mean * mean));
            if (deviation > spacing * 0.1) return null;
            tones[level] = new(centers[level] + mean, count / (double)sortedFrequencies.Count, deviation);
            assigned += count;
        }
        if (assigned < sortedFrequencies.Count * 0.65) return null;
        spacing = (tones[^1].FrequencyHz - tones[0].FrequencyHz) / (tones.Length - 1);
        for (int index = 1; index < tones.Length; index++)
            if (Math.Abs(tones[index].FrequencyHz - tones[index - 1].FrequencyHz - spacing) > spacing * 0.2) return null;
        return new(tones, (tones[0].FrequencyHz + tones[^1].FrequencyHz) / 2, spacing);
    }
}
