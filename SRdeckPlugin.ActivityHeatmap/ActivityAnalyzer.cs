namespace SRdeckPlugin.ActivityHeatmap;

public sealed record ActivitySnapshot(long CenterFrequencyHz, int SampleRateHz, int ThresholdDbm,
    int RowDurationSeconds, DateTimeOffset[] RowTimes, float[] Activity, int[] Observations, int[] CellObservations)
{
    public double LowerFrequencyHz { get; init; } = CenterFrequencyHz - SampleRateHz / 2.0;
    public double UpperFrequencyHz { get; init; } = CenterFrequencyHz + SampleRateHz / 2.0;
    public double CellWidthHz => (UpperFrequencyHz - LowerFrequencyHz) / ActivityAnalyzer.Columns;
}

/// <summary>Sample-clock occupancy measurements. The caller owns continuity and synchronization.</summary>
public sealed class ActivityAnalyzer
{
    public const int Columns = 256;
    public const int HistoryMinutes = 24 * 60;
    private readonly int[] hits = new int[HistoryMinutes * Columns];
    private readonly int[] observations = new int[HistoryMinutes];
    private readonly int[] cellObservations = new int[HistoryMinutes * Columns];
    private int sampleRate;
    private long position, latestMinute = -1;
    private long centerFrequency;
    private double lowerFrequency, upperFrequency;
    private DateTimeOffset origin;

    public long ProcessedSamples => position;

    public void Reset()
    {
        sampleRate = 0;
        position = 0;
        latestMinute = -1;
        Array.Clear(hits);
        Array.Clear(observations);
        Array.Clear(cellObservations);
    }

    public void Discontinue(long droppedSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(droppedSamples);
        position = checked(position + droppedSamples);
    }

    public void Push(ReadOnlySpan<float> levelsDbm, int sampleCount, int sampleRateHz, long centerFrequencyHz,
        DateTimeOffset startTime, int thresholdDbm, double? lowerFrequencyHz = null, double? upperFrequencyHz = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        if (!levelsDbm.IsEmpty && levelsDbm.Length != Columns) throw new ArgumentException("Invalid spectrum width.", nameof(levelsDbm));
        double lower = lowerFrequencyHz ?? centerFrequencyHz - sampleRateHz / 2.0;
        double upper = upperFrequencyHz ?? centerFrequencyHz + sampleRateHz / 2.0;
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower >= upper)
            throw new ArgumentException("Invalid spectrum frequency range.");
        if (sampleRate != sampleRateHz || centerFrequency != centerFrequencyHz || lowerFrequency != lower || upperFrequency != upper)
        {
            Reset();
            sampleRate = sampleRateHz;
            centerFrequency = centerFrequencyHz;
            lowerFrequency = lower;
            upperFrequency = upper;
            origin = startTime;
        }
        long minute = position / sampleRate / 60;
        AdvanceRows(minute);
        int row = (int)(minute % HistoryMinutes);
        bool observed = false;
        for (int column = 0; column < levelsDbm.Length; column++)
        {
            if (!float.IsFinite(levelsDbm[column])) continue;
            observed = true;
            cellObservations[row * Columns + column]++;
            if (levelsDbm[column] >= thresholdDbm) hits[row * Columns + column]++;
        }
        if (observed) observations[row]++;
        position = checked(position + sampleCount);
    }

    private void AdvanceRows(long minute)
    {
        for (long m = Math.Max(latestMinute + 1, minute - HistoryMinutes + 1); m <= minute; m++)
        {
            int row = (int)(m % HistoryMinutes);
            observations[row] = 0;
            Array.Clear(hits, row * Columns, Columns);
            Array.Clear(cellObservations, row * Columns, Columns);
        }
        latestMinute = minute;
    }

    public ActivitySnapshot? CreateSnapshot(int thresholdDbm, int historyHours = 6)
    {
        if (historyHours is not (1 or 6 or 24)) throw new ArgumentOutOfRangeException(nameof(historyHours));
        if (latestMinute < 0) return null;
        int minutesPerRow = historyHours == 24 ? 4 : 1;
        int rows = historyHours * 60 / minutesPerRow;
        var times = new DateTimeOffset[rows];
        var activity = new float[rows * Columns];
        var counts = new int[rows];
        var cellCounts = new int[rows * Columns];
        long newestMinute = latestMinute / minutesPerRow * minutesPerRow;
        for (int n = 0; n < rows; n++)
        {
            long firstMinute = newestMinute - n * minutesPerRow;
            times[n] = origin.AddMinutes(firstMinute);
            for (long m = firstMinute; m < firstMinute + minutesPerRow; m++)
            {
                if (m < 0 || m > latestMinute || m <= latestMinute - HistoryMinutes) continue;
                int row = (int)(m % HistoryMinutes);
                counts[n] += observations[row];
                for (int c = 0; c < Columns; c++)
                {
                    activity[n * Columns + c] += hits[row * Columns + c];
                    cellCounts[n * Columns + c] += cellObservations[row * Columns + c];
                }
            }
            for (int c = 0; c < Columns; c++)
                activity[n * Columns + c] = cellCounts[n * Columns + c] == 0 ? float.NaN : activity[n * Columns + c] / cellCounts[n * Columns + c];
        }
        return new(centerFrequency, sampleRate, thresholdDbm, minutesPerRow * 60, times, activity, counts, cellCounts)
        { LowerFrequencyHz = lowerFrequency, UpperFrequencyHz = upperFrequency };
    }
}
