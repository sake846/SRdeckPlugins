using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.Protocols;

namespace SRdeckPlugin.Ft8.Dsp;

/// <summary>Whole-passband FT4 synchronizer and LDPC decoder.</summary>
public sealed class Ft4Decoder
{
    public const int SampleRateHz = 12_000;
    public const int SymbolSamples = 576;
    public const int FrameSymbols = 103;
    private const int FftSize = 1024;
    private const int HopSamples = SymbolSamples / 2;
    private const double ToneSpacingHz = 12000.0 / SymbolSamples;
    private const double BinWidthHz = SampleRateHz / (double)FftSize;
    private const int MaximumFineCandidates = 24;
    private static readonly int[][] Costas =
    [
        [0, 1, 3, 2],
        [1, 0, 2, 3],
        [2, 3, 1, 0],
        [3, 2, 0, 1]
    ];
    private static readonly int[] SyncStarts = [0, 33, 66, 99];
    private static readonly int[] Gray = [0, 1, 3, 2];
    private readonly Ft8Codec codec = new();
    private readonly Radix2Fft fft = new(FftSize);
    private readonly float[] real = new float[FftSize];
    private readonly float[] imaginary = new float[FftSize];
    private readonly float[] window = CreateWindow();

    public Ft8Decoder.DecodeBatch DecodeSlot(ReadOnlySpan<Complex32> input,
        DateTimeOffset slotStart, Guid streamId, long channelCenterFrequencyHz,
        Ft8Settings settings, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Complex32[] samples = Resample(input, Ft8Receiver.OutputSampleRateHz, SampleRateHz);
        if (samples.Length < SampleRateHz * 6)
            return new([], 0, 0, 0, stopwatch.Elapsed);

        Spectrogram spectrum = CreateSpectrogram(samples);
        List<Candidate> candidates = FindCandidates(spectrum, settings);
        int ldpcRejected = 0;
        int crcRejected = 0;
        var decoded = new List<Ft8Reception>();
        var locations = new List<(string Payload, int Frequency, double Time)>();
        float[] likelihood = new float[Ft8Codec.CodewordBits];

        foreach (Candidate coarse in candidates.Take(MaximumFineCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate candidate = Refine(samples, coarse);
            ExtractLikelihood(samples, candidate, likelihood, out double signalPower,
                out double noisePower);
            bool normalized = Normalize(likelihood);
            Ft8Codec.DecodedMessage? message = null;
            int parityErrors = int.MaxValue;
            bool crcValid = false;
            if (!normalized || !codec.TryDecode(likelihood, settings.LdpcIterations,
                    out message, out parityErrors, out crcValid))
            {
                if (!normalized || parityErrors != 0) ldpcRejected++;
                else if (!crcValid) crcRejected++;
                continue;
            }

            double timeOffset = candidate.StartSample / (double)SampleRateHz;
            int audioFrequency = (int)Math.Round(Ft8Receiver.AudioCenterHz + candidate.FrequencyHz);
            string fingerprint = Convert.ToHexString(message!.Payload);
            if (locations.Any(item => item.Payload == fingerprint &&
                    Math.Abs(item.Frequency - audioFrequency) <= 100 &&
                    Math.Abs(item.Time - timeOffset) <= 0.5))
                continue;
            locations.Add((fingerprint, audioFrequency, timeOffset));
            double ratio = Math.Max(1e-6, signalPower / Math.Max(noisePower, 1e-20) - 1);
            int snr = Math.Max(-24, (int)Math.Round(10 * Math.Log10(ratio) -
                10 * Math.Log10(2500.0 / 83.3)));
            decoded.Add(new Ft8Reception(slotStart, slotStart.AddSeconds(Math.Max(0, timeOffset)),
                streamId, channelCenterFrequencyHz + (long)Math.Round(candidate.FrequencyHz),
                audioFrequency, timeOffset, snr, candidate.Score, message.Text, message.Type,
                message.FromCall, message.ToCall, message.Extra, message.Payload,
                WeakSignalMode.FT4));
        }

        stopwatch.Stop();
        return new(decoded.OrderBy(item => item.AudioFrequencyHz).ToArray(),
            candidates.Count, ldpcRejected, crcRejected, stopwatch.Elapsed);
    }

    private Spectrogram CreateSpectrogram(ReadOnlySpan<Complex32> samples)
    {
        int blocks = 1 + (samples.Length - SymbolSamples) / HopSamples;
        int minimumBin = (int)Math.Floor((Ft8Decoder.AudioMinimumHz - Ft8Receiver.AudioCenterHz) /
            BinWidthHz) - 1;
        int maximumBin = (int)Math.Ceiling((Ft8Decoder.AudioMaximumHz - Ft8Receiver.AudioCenterHz) /
            BinWidthHz) + 2;
        int binCount = maximumBin - minimumBin;
        var powerDb = new float[blocks * binCount];
        for (int block = 0; block < blocks; block++)
        {
            int start = block * HopSamples;
            Array.Clear(real);
            Array.Clear(imaginary);
            for (int index = 0; index < SymbolSamples; index++)
            {
                Complex32 sample = samples[start + index];
                real[index] = sample.I * window[index];
                imaginary[index] = sample.Q * window[index];
            }
            fft.Transform(real, imaginary);
            for (int bin = minimumBin; bin < maximumBin; bin++)
            {
                int fftBin = (bin % FftSize + FftSize) % FftSize;
                double power = real[fftBin] * real[fftBin] +
                               imaginary[fftBin] * imaginary[fftBin];
                powerDb[block * binCount + bin - minimumBin] =
                    (float)(10 * Math.Log10(Math.Max(power, 1e-20)));
            }
        }
        return new(powerDb, blocks, minimumBin, binCount);
    }

    private static List<Candidate> FindCandidates(Spectrogram spectrum, Ft8Settings settings)
    {
        var all = new List<Candidate>();
        int maximumStartHalf = Math.Min(52, spectrum.Blocks - FrameSymbols * 2);
        for (int startHalf = -4; startHalf <= maximumStartHalf; startHalf++)
        for (int baseBin = spectrum.MinimumBin;
             baseBin + (int)Math.Ceiling(3 * ToneSpacingHz / BinWidthHz) < spectrum.MaximumBin;
             baseBin++)
        {
            double expected = 0;
            double alternatives = 0;
            int count = 0;
            for (int group = 0; group < 4; group++)
            for (int index = 0; index < 4; index++)
            {
                int block = startHalf + 2 * (SyncStarts[group] + index);
                if (block < 0 || block >= spectrum.Blocks) continue;
                int expectedTone = Costas[group][index];
                expected += spectrum.Get(block, baseBin +
                    (int)Math.Round(expectedTone * ToneSpacingHz / BinWidthHz));
                double other = 0;
                for (int tone = 0; tone < 4; tone++)
                    if (tone != expectedTone)
                        other += spectrum.Get(block, baseBin +
                            (int)Math.Round(tone * ToneSpacingHz / BinWidthHz));
                alternatives += other / 3;
                count++;
            }
            if (count == 0) continue;
            int score = (int)Math.Round((expected - alternatives) / count);
            if (score >= settings.MinimumSyncScore)
                all.Add(new Candidate(score, startHalf * HopSamples,
                    baseBin * BinWidthHz));
        }

        var selected = new List<Candidate>();
        foreach (Candidate candidate in all.OrderByDescending(item => item.Score))
        {
            if (selected.Any(item => Math.Abs(item.StartSample - candidate.StartSample) <= HopSamples &&
                    Math.Abs(item.FrequencyHz - candidate.FrequencyHz) <= ToneSpacingHz))
                continue;
            selected.Add(candidate);
            if (selected.Count >= settings.MaximumCandidates) break;
        }
        return selected;
    }

    private static Candidate Refine(ReadOnlySpan<Complex32> samples, Candidate coarse)
    {
        Candidate best = coarse;
        double bestMetric = double.NegativeInfinity;
        for (int time = coarse.StartSample - HopSamples / 2;
             time <= coarse.StartSample + HopSamples / 2; time += HopSamples / 2)
        for (double frequency = coarse.FrequencyHz - BinWidthHz;
             frequency <= coarse.FrequencyHz + BinWidthHz; frequency += BinWidthHz / 2)
        {
            double metric = 0;
            int count = 0;
            for (int group = 0; group < 4; group++)
            for (int index = 0; index < 4; index++)
            {
                int symbolStart = time + (SyncStarts[group] + index) * SymbolSamples;
                if (symbolStart < 0 || symbolStart + SymbolSamples > samples.Length) continue;
                metric += Math.Log(Math.Max(TonePower(samples, symbolStart,
                    frequency + Costas[group][index] * ToneSpacingHz), 1e-20));
                count++;
            }
            if (count == 0 || metric <= bestMetric) continue;
            bestMetric = metric;
            best = new Candidate(coarse.Score, time, frequency);
        }
        return best;
    }

    private static void ExtractLikelihood(ReadOnlySpan<Complex32> samples,
        Candidate candidate, Span<float> likelihood, out double signalPower,
        out double noisePower)
    {
        signalPower = 0;
        noisePower = 0;
        int dataSymbol = 0;
        Span<double> tones = stackalloc double[4];
        for (int channelSymbol = 0; channelSymbol < FrameSymbols; channelSymbol++)
        {
            if (IsSyncSymbol(channelSymbol)) continue;
            int start = candidate.StartSample + channelSymbol * SymbolSamples;
            int bit = dataSymbol * 2;
            dataSymbol++;
            if (start < 0 || start + SymbolSamples > samples.Length)
            {
                likelihood.Slice(bit, 2).Clear();
                continue;
            }
            for (int tone = 0; tone < 4; tone++)
                tones[tone] = TonePower(samples, start,
                    candidate.FrequencyHz + tone * ToneSpacingHz);
            double p0 = tones[Gray[0]], p1 = tones[Gray[1]];
            double p2 = tones[Gray[2]], p3 = tones[Gray[3]];
            likelihood[bit] = (float)(10 * Math.Log10(Math.Max(p2, p3) + 1e-20) -
                                      10 * Math.Log10(Math.Max(p0, p1) + 1e-20));
            likelihood[bit + 1] = (float)(10 * Math.Log10(Math.Max(p1, p3) + 1e-20) -
                                          10 * Math.Log10(Math.Max(p0, p2) + 1e-20));
            double maximum = Math.Max(Math.Max(tones[0], tones[1]), Math.Max(tones[2], tones[3]));
            signalPower += maximum;
            noisePower += (tones[0] + tones[1] + tones[2] + tones[3] - maximum) / 3;
        }
        signalPower /= 87;
        noisePower /= 87;
    }

    private static bool IsSyncSymbol(int symbol) =>
        SyncStarts.Any(start => symbol >= start && symbol < start + 4);

    private static double TonePower(ReadOnlySpan<Complex32> samples, int start, double frequency)
    {
        double angle = -2 * Math.PI * frequency / SampleRateHz;
        double rotationReal = Math.Cos(angle), rotationImaginary = Math.Sin(angle);
        double oscillatorReal = 1, oscillatorImaginary = 0, sumReal = 0, sumImaginary = 0;
        for (int index = 0; index < SymbolSamples; index++)
        {
            Complex32 sample = samples[start + index];
            double weight = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (index + 0.5) / SymbolSamples);
            sumReal += weight * (sample.I * oscillatorReal - sample.Q * oscillatorImaginary);
            sumImaginary += weight * (sample.I * oscillatorImaginary + sample.Q * oscillatorReal);
            double next = oscillatorReal * rotationReal - oscillatorImaginary * rotationImaginary;
            oscillatorImaginary = oscillatorImaginary * rotationReal + oscillatorReal * rotationImaginary;
            oscillatorReal = next;
        }
        return sumReal * sumReal + sumImaginary * sumImaginary;
    }

    private static bool Normalize(Span<float> likelihood)
    {
        double sum = 0, sumSquared = 0;
        foreach (float value in likelihood) { sum += value; sumSquared += value * value; }
        double variance = (sumSquared - sum * sum / likelihood.Length) / likelihood.Length;
        if (variance < 1e-9 || !double.IsFinite(variance)) return false;
        float scale = (float)Math.Sqrt(24 / variance);
        for (int index = 0; index < likelihood.Length; index++) likelihood[index] *= scale;
        return true;
    }

    internal static Complex32[] Resample(ReadOnlySpan<Complex32> input, int inputRate, int outputRate)
    {
        if (inputRate == outputRate) return input.ToArray();
        int count = (int)Math.Floor(input.Length * (double)outputRate / inputRate);
        var output = new Complex32[count];
        double step = inputRate / (double)outputRate;
        for (int index = 0; index < count; index++)
        {
            double position = index * step;
            int left = Math.Min((int)position, input.Length - 1);
            int right = Math.Min(left + 1, input.Length - 1);
            float fraction = (float)(position - left);
            output[index] = new Complex32(
                input[left].I + (input[right].I - input[left].I) * fraction,
                input[left].Q + (input[right].Q - input[left].Q) * fraction);
        }
        return output;
    }

    private static float[] CreateWindow()
    {
        var result = new float[SymbolSamples];
        for (int index = 0; index < result.Length; index++)
            result[index] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (index + 0.5) / result.Length));
        return result;
    }

    private readonly record struct Candidate(int Score, int StartSample, double FrequencyHz);
    private sealed record Spectrogram(float[] PowerDb, int Blocks, int MinimumBin, int BinCount)
    {
        public int MaximumBin => MinimumBin + BinCount;
        public float Get(int block, int bin) => block < 0 || block >= Blocks ||
            bin < MinimumBin || bin >= MaximumBin ? -200 :
            PowerDb[block * BinCount + bin - MinimumBin];
    }
}
