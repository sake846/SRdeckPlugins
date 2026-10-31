using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.SignalWorkbench;

public sealed record SymbolRateCandidate(double RateHz, double ProminenceDb, string Evidence)
{
    public double? ModulationIndex { get; init; }
    public string Rate => $"{RateHz:N1} sym/s";
    public string Prominence => $"{ProminenceDb:F1} dB";
}

public sealed record SignalSnapshot(
    int SampleRateHz, double LevelDbfs, Complex32[] Constellation,
    int[] FrequencyHistogram, double HistogramHalfRangeHz, int FrequencySampleCount,
    double MeanFrequencyHz, double FrequencySpreadHz, SymbolRateCandidate[] Candidates)
{
    public double DurationSeconds => SignalAnalyzer.FrameSize / (double)SampleRateHz;
    public double ResolutionHz => SampleRateHz / (double)SignalAnalyzer.FrameSize;
    public FskMeasurements? Fsk { get; init; }
    public float[] FrequencyTraceHz { get; init; } = [];
}

/// <summary>Bounded, unsynchronized baseband measurements; callers own continuity and locking.</summary>
public sealed class SignalAnalyzer
{
    public const int FrameSize = 16_384;
    private readonly Complex32[] samples = new Complex32[FrameSize];
    private readonly Radix2Fft fft = new(FrameSize);
    private readonly float[] real = new float[FrameSize];
    private readonly float[] imaginary = new float[FrameSize];
    private readonly double[] power = new double[FrameSize];
    private readonly double[] phase = new double[FrameSize];
    private readonly bool[] validPhase = new bool[FrameSize];
    private int count;
    private int skipped;
    private int sampleRate;

    public void Reset()
    {
        count = skipped = sampleRate = 0;
    }

    public SignalSnapshot? Push(ReadOnlySpan<Complex32> input, int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        if (sampleRate != sampleRateHz)
        {
            Reset();
            sampleRate = sampleRateHz;
        }
        SignalSnapshot? latest = null;
        foreach (Complex32 sample in input)
        {
            if (!float.IsFinite(sample.I) || !float.IsFinite(sample.Q))
            {
                // Do not join samples across a corrupt input value.
                count = skipped = 0;
                latest = null;
                continue;
            }
            if (skipped > 0) { skipped--; continue; }
            samples[count++] = sample;
            if (count != FrameSize) continue;
            latest = Analyze();
            count = 0;
            skipped = Math.Max(0, sampleRateHz / 4 - FrameSize);
        }
        return latest;
    }

    private SignalSnapshot Analyze()
    {
        double meanPower = 0;
        for (int n = 0; n < FrameSize; n++)
        {
            Complex32 s = samples[n];
            power[n] = (double)s.I * s.I + (double)s.Q * s.Q;
            meanPower += power[n] / FrameSize;
        }
        double level = 10 * Math.Log10(Math.Max(1e-16, meanPower));
        if (meanPower < 1e-16)
            return new(sampleRate, level, [], new int[128], sampleRate / 2.0, 0, 0, 0, []);

        double scale = 1 / Math.Sqrt(meanPower);
        var points = new Complex32[1_536];
        for (int n = 0; n < points.Length; n++)
        {
            Complex32 s = samples[n * FrameSize / points.Length];
            points[n] = new((float)(s.I * scale), (float)(s.Q * scale));
        }

        var frequencies = new List<double>(FrameSize);
        double sumSin = 0, sumCos = 0;
        Array.Clear(validPhase);
        for (int n = 1; n < FrameSize; n++)
        {
            if (power[n] < meanPower * 0.001 || power[n - 1] < meanPower * 0.001) continue;
            Complex32 a = samples[n - 1], b = samples[n];
            phase[n] = Math.Atan2((double)a.I * b.Q - (double)a.Q * b.I,
                (double)a.I * b.I + (double)a.Q * b.Q);
            validPhase[n] = true;
            sumSin += Math.Sin(phase[n]);
            sumCos += Math.Cos(phase[n]);
            frequencies.Add(phase[n] * sampleRate / (2 * Math.PI));
        }
        frequencies.Sort();
        double meanFrequency = frequencies.Count == 0 ? 0 : frequencies.Average();
        double spread = frequencies.Count == 0 ? 0 : Quantile(0.95) - Quantile(0.05);
        double halfRange = frequencies.Count == 0 ? sampleRate / 2.0 :
            Math.Min(sampleRate / 2.0, Math.Max(sampleRate * 4.0 / FrameSize,
                Math.Max(Math.Abs(Quantile(0.01)), Math.Abs(Quantile(0.99))) * 1.1));
        var histogram = new int[128];
        foreach (double frequency in frequencies)
        {
            int bin = (int)((frequency + halfRange) / (2 * halfRange) * histogram.Length);
            histogram[Math.Clamp(bin, 0, histogram.Length - 1)]++;
        }

        var candidates = new List<SymbolRateCandidate>();
        double averagePhase = Math.Atan2(sumSin, sumCos);
        AddSpectralCandidates(n => power[n] / meanPower, "包絡電力", candidates);
        AddSpectralCandidates(n => validPhase[n] ? 1 - Math.Cos(phase[n] - averagePhase) : 0,
            "位相変化", candidates);
        AddSpectralCandidates(n => n > 1 && validPhase[n] && validPhase[n - 1]
            ? Math.Pow(Math.IEEERemainder(phase[n] - phase[n - 1], 2 * Math.PI), 2) : 0,
            "周波数変化", candidates);
        FskMeasurements? fsk = FskAnalyzer.Analyze(frequencies, sampleRate / (double)FrameSize);
        if (fsk is not null)
        {
            // Equal-weight tone changes keep large jumps from dominating the clock feature.
            var levels = new int[FrameSize];
            for (int n = 0; n < levels.Length; n++)
            {
                levels[n] = -1;
                if (!validPhase[n]) continue;
                double frequency = phase[n] * sampleRate / (2 * Math.PI), distance = double.MaxValue;
                for (int tone = 0; tone < fsk.Tones.Length; tone++)
                {
                    double delta = Math.Abs(frequency - fsk.Tones[tone].FrequencyHz);
                    if (delta >= distance) continue;
                    distance = delta;
                    levels[n] = tone;
                }
            }
            AddSpectralCandidates(n => n > 1 && levels[n] >= 0 && levels[n - 1] >= 0 &&
                levels[n] != levels[n - 1] ? 1 : 0, "FSKトーン遷移", candidates);
        }
        var distinct = new List<SymbolRateCandidate>();
        foreach (SymbolRateCandidate candidate in candidates.OrderByDescending(c => c.ProminenceDb))
        {
            if (distinct.Any(c => Math.Abs(c.RateHz - candidate.RateHz) <
                Math.Max(sampleRate * 3.0 / FrameSize, candidate.RateHz * 0.015))) continue;
            distinct.Add(candidate);
            if (distinct.Count == 5) break;
        }
        if (fsk is not null)
            for (int n = 0; n < distinct.Count; n++)
                distinct[n] = distinct[n] with { ModulationIndex = fsk.ToneSpacingHz / distinct[n].RateHz };
        var trace = new float[1_024];
        for (int n = 0; n < trace.Length; n++)
            trace[n] = validPhase[n] ? (float)(phase[n] * sampleRate / (2 * Math.PI)) : float.NaN;
        return new(sampleRate, level, points, histogram, halfRange, frequencies.Count,
            meanFrequency, spread, distinct.ToArray()) { Fsk = fsk, FrequencyTraceHz = trace };

        double Quantile(double fraction) => frequencies[(int)((frequencies.Count - 1) * fraction)];
    }

    private void AddSpectralCandidates(Func<int, double> feature, string evidence,
        List<SymbolRateCandidate> candidates)
    {
        double mean = 0, variance = 0;
        for (int n = 0; n < FrameSize; n++) mean += feature(n) / FrameSize;
        for (int n = 0; n < FrameSize; n++)
        {
            double value = feature(n) - mean;
            variance += value * value / FrameSize;
            real[n] = (float)(value * (0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (FrameSize - 1))));
        }
        // Reject constant features and float-rounding residue from unmodulated carriers.
        if (variance < 1e-8) return;
        Array.Clear(imaginary);
        fft.Transform(real, imaginary);
        var spectrum = new double[FrameSize / 4 + 1];
        for (int k = 1; k < spectrum.Length; k++)
            spectrum[k] = (double)real[k] * real[k] + (double)imaginary[k] * imaginary[k];
        double[] background = spectrum[4..^1];
        double totalPower = background.Sum();
        Array.Sort(background);
        double floor = Math.Max(1e-20, background[background.Length / 2]);
        for (int k = 4; k < spectrum.Length - 1; k++)
        {
            if (spectrum[k] <= spectrum[k - 1] || spectrum[k] < spectrum[k + 1]) continue;
            double prominence = 10 * Math.Log10(spectrum[k] / floor);
            double concentration = (spectrum[k - 1] + spectrum[k] + spectrum[k + 1]) / totalPower;
            if (prominence < 12 || concentration < 0.015) continue;
            candidates.Add(new(k * sampleRate / (double)FrameSize, prominence, evidence));
        }
    }
}
