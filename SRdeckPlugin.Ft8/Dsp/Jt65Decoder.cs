using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.Protocols;

namespace SRdeckPlugin.Ft8.Dsp;

/// <summary>Wide-passband JT65A synchronizer, 65-FSK demodulator, and RS decoder.</summary>
public sealed class Jt65Decoder
{
    public const int SampleRateHz = 12_000;
    public const int FrameSymbols = 126;
    public const double ToneSpacingHz = 11025.0 / 4096.0;
    public const double SymbolDurationSeconds = 4096.0 / 11025.0;
    private const int FftSize = 8192;
    private const double BinWidthHz = SampleRateHz / (double)FftSize;
    private const int MaximumDecodeCandidates = 64;
    private static readonly int[] SyncPattern =
    [
        1,0,0,1,1,0,0,0,1,1,1,1,1,1,0,1,0,1,0,0,
        0,1,0,1,1,0,0,1,0,0,0,1,1,1,0,0,1,1,1,1,
        0,1,1,0,1,1,1,1,0,0,0,1,1,0,1,0,1,0,1,1,
        0,0,1,1,0,1,0,1,0,1,0,0,1,0,0,0,0,0,0,1,
        1,0,0,0,0,0,0,0,1,1,0,1,0,0,1,0,1,1,0,1,
        0,1,0,1,0,0,1,1,0,0,1,0,0,1,0,0,0,0,1,1,
        1,1,1,1,1,1
    ];
    private readonly Jt65Codec codec = new();
    private readonly Radix2Fft fft = new(FftSize);
    private readonly float[] real = new float[FftSize];
    private readonly float[] imaginary = new float[FftSize];

    public Ft8Decoder.DecodeBatch DecodeSlot(ReadOnlySpan<Complex32> input,
        DateTimeOffset slotStart, Guid streamId, long channelCenterFrequencyHz,
        Ft8Settings settings, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Complex32[] samples = Ft4Decoder.Resample(input, Ft8Receiver.OutputSampleRateHz,
            SampleRateHz);
        if (samples.Length < SampleRateHz * 48)
            return new([], 0, 0, 0, stopwatch.Elapsed);

        Spectrogram spectrum = CreateSpectrogram(samples);
        List<Candidate> candidates = FindCandidates(spectrum, settings);
        var decoded = new List<Ft8Reception>();
        var locations = new List<(string Text, int Frequency, double Time)>();
        int rsRejected = 0;
        var channelSymbols = new byte[63];

        foreach (Candidate candidate in candidates.Take(MaximumDecodeCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(channelSymbols);
            int dataIndex = 0;
            double signal = 0;
            double noise = 0;
            for (int symbol = 0; symbol < FrameSymbols; symbol++)
            {
                if (SyncPattern[symbol] != 0) continue;
                int block = candidate.StartHalfSymbol + symbol * 2;
                int bestTone = 0;
                float bestPower = float.NegativeInfinity;
                double sum = 0;
                for (int tone = 0; tone < 64; tone++)
                {
                    int bin = candidate.BaseBin + (int)Math.Round((tone + 2) *
                        ToneSpacingHz / BinWidthHz);
                    float value = spectrum.Get(block, bin);
                    sum += Math.Pow(10, value / 10.0);
                    if (value <= bestPower) continue;
                    bestPower = value;
                    bestTone = tone;
                }
                channelSymbols[dataIndex++] = (byte)bestTone;
                double bestLinear = Math.Pow(10, bestPower / 10.0);
                signal += bestLinear;
                noise += Math.Max(1e-20, (sum - bestLinear) / 63);
            }

            if (!codec.TryDecode(channelSymbols, out Jt65Codec.DecodedMessage? message,
                    out _))
            {
                rsRejected++;
                continue;
            }
            double timeOffset = candidate.StartHalfSymbol * SymbolDurationSeconds / 2;
            double basebandFrequency = candidate.BaseBin * BinWidthHz;
            int audioFrequency = (int)Math.Round(Ft8Receiver.AudioCenterHz + basebandFrequency);
            if (locations.Any(item => item.Text == message!.Text &&
                    Math.Abs(item.Frequency - audioFrequency) <= 200 &&
                    Math.Abs(item.Time - timeOffset) <= 1.0))
                continue;
            locations.Add((message!.Text, audioFrequency, timeOffset));
            double ratio = Math.Max(1e-6, signal / Math.Max(noise, 1e-20));
            int snr = Math.Max(-30, (int)Math.Round(10 * Math.Log10(ratio) -
                10 * Math.Log10(2500.0 / 177.6)));
            decoded.Add(new Ft8Reception(slotStart, slotStart.AddSeconds(Math.Max(0, timeOffset)),
                streamId, channelCenterFrequencyHz + (long)Math.Round(basebandFrequency),
                audioFrequency, timeOffset, snr, candidate.Score, message.Text, message.Type,
                message.FromCall, message.ToCall, message.Extra, message.Payload,
                WeakSignalMode.JT65));
        }

        stopwatch.Stop();
        return new(decoded.OrderBy(item => item.AudioFrequencyHz).ToArray(), candidates.Count,
            rsRejected, 0, stopwatch.Elapsed);
    }

    private Spectrogram CreateSpectrogram(ReadOnlySpan<Complex32> samples)
    {
        int halfBlocks = 1 + (int)Math.Floor(samples.Length /
            (SampleRateHz * SymbolDurationSeconds / 2));
        int symbolSamples = (int)Math.Round(SampleRateHz * SymbolDurationSeconds);
        int minimumBin = (int)Math.Floor((Ft8Decoder.AudioMinimumHz - Ft8Receiver.AudioCenterHz) /
            BinWidthHz) - 1;
        int maximumBin = (int)Math.Ceiling((Ft8Decoder.AudioMaximumHz - Ft8Receiver.AudioCenterHz) /
            BinWidthHz) + 2;
        int binCount = maximumBin - minimumBin;
        var powerDb = new float[halfBlocks * binCount];

        for (int block = 0; block < halfBlocks; block++)
        {
            int start = (int)Math.Round(block * SampleRateHz * SymbolDurationSeconds / 2);
            Array.Clear(real);
            Array.Clear(imaginary);
            int available = Math.Min(symbolSamples, samples.Length - start);
            if (available <= 0) break;
            for (int index = 0; index < available; index++)
            {
                Complex32 sample = samples[start + index];
                float window = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI *
                    (index + 0.5) / symbolSamples));
                real[index] = sample.I * window;
                imaginary[index] = sample.Q * window;
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
        return new(powerDb, halfBlocks, minimumBin, binCount);
    }

    private static List<Candidate> FindCandidates(Spectrogram spectrum, Ft8Settings settings)
    {
        var all = new List<Candidate>();
        int maximumStart = Math.Min(32, spectrum.Blocks - FrameSymbols * 2);
        int highestBase = spectrum.MaximumBin -
            (int)Math.Ceiling(65 * ToneSpacingHz / BinWidthHz) - 1;
        for (int startHalf = -4; startHalf <= maximumStart; startHalf++)
        for (int baseBin = spectrum.MinimumBin; baseBin <= highestBase; baseBin++)
        {
            double sync = 0, data = 0;
            int syncCount = 0, dataCount = 0;
            for (int symbol = 0; symbol < FrameSymbols; symbol++)
            {
                int block = startHalf + symbol * 2;
                if (block < 0 || block >= spectrum.Blocks) continue;
                if (SyncPattern[symbol] != 0)
                {
                    sync += spectrum.Get(block, baseBin);
                    syncCount++;
                }
                else
                {
                    data += spectrum.Get(block, baseBin);
                    dataCount++;
                }
            }
            if (syncCount < 40 || dataCount < 40) continue;
            int score = (int)Math.Round(sync / syncCount - data / dataCount);
            if (score >= settings.MinimumSyncScore)
                all.Add(new Candidate(score, startHalf, baseBin));
        }

        var selected = new List<Candidate>();
        foreach (Candidate candidate in all.OrderByDescending(item => item.Score))
        {
            if (selected.Any(item => Math.Abs(item.StartHalfSymbol - candidate.StartHalfSymbol) <= 2 &&
                    Math.Abs(item.BaseBin - candidate.BaseBin) <= 2))
                continue;
            selected.Add(candidate);
            if (selected.Count >= settings.MaximumCandidates) break;
        }
        return selected;
    }

    private readonly record struct Candidate(int Score, int StartHalfSymbol, int BaseBin);
    private sealed record Spectrogram(float[] PowerDb, int Blocks, int MinimumBin, int BinCount)
    {
        public int MaximumBin => MinimumBin + BinCount;
        public float Get(int block, int bin) => block < 0 || block >= Blocks ||
            bin < MinimumBin || bin >= MaximumBin ? -200 :
            PowerDb[block * BinCount + bin - MinimumBin];
    }
}
