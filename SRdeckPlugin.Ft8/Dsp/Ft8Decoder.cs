using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.Protocols;

namespace SRdeckPlugin.Ft8.Dsp;

/// <summary>
/// Whole-passband FT8 candidate detector and soft-decision decoder.
/// The synchronizer, demapper, and cancellation pass are implemented from
/// the published FT8 modulation and decoding definitions; see docs/ft8-protocol.md.
/// </summary>
public sealed partial class Ft8Decoder
{
    public const int SampleRateHz = 12_800;
    public const int SlotSamples = SampleRateHz * 15;
    public const int AudioCenterHz = 1_600;
    public const int AudioMinimumHz = 200;
    public const int AudioMaximumHz = 3_000;
    private const int SymbolSamples = 2048;
    private const int TimeOversampling = 2;
    private const int FrequencyOversampling = 2;
    private const int FftSize = SymbolSamples * FrequencyOversampling;
    private const int HopSamples = SymbolSamples / TimeOversampling;
    private const int SnrBaselineFftSize = 4096;
    // Fine synchronization performs direct matched filtering over an entire
    // 12.64-second frame.  It is substantially more expensive than coarse
    // waterfall decoding, so reserve it for the strongest hypotheses while
    // still refining every coarse LDPC success.
    private const int FineSynchronizationCandidateLimit = 96;
    private const double SnrBaselineBinWidthHz = SampleRateHz / (double)SnrBaselineFftSize;
    private const double WsjtSnrCalibrationDb = 27.0;
    private const int WsjtMinimumSnrDb = -25;
    private static readonly int[] Costas = [3, 1, 4, 0, 6, 5, 2];
    private static readonly int[] Gray = [0, 1, 3, 2, 5, 6, 4, 7];
    private readonly Ft8Codec codec = new();
    private readonly Radix2Fft fft = new(FftSize);
    private readonly Radix2Fft snrBaselineFft = new(SnrBaselineFftSize);
    private readonly float[] window = CreateWindow();
    private readonly float[] fineWindow = CreateFineWindow();
    private readonly float[] snrBaselineWindow = CreateSnrBaselineWindow();
    private readonly float[] real = new float[FftSize];
    private readonly float[] imaginary = new float[FftSize];
    private readonly float[] snrBaselineReal = new float[SnrBaselineFftSize];
    private readonly float[] snrBaselineImaginary = new float[SnrBaselineFftSize];
    private readonly double fineWindowEnergy;
    private readonly double snrBaselineWindowEnergy;
    private readonly bool enableFineSynchronization;
    private readonly bool enableCancellationPass;

    public Ft8Decoder() : this(true, true)
    {
    }

    internal Ft8Decoder(bool enableFineSynchronization, bool enableCancellationPass)
    {
        this.enableFineSynchronization = enableFineSynchronization;
        this.enableCancellationPass = enableCancellationPass;
        fineWindowEnergy = fineWindow.Sum(sample => (double)sample * sample);
        snrBaselineWindowEnergy = snrBaselineWindow.Sum(sample => (double)sample * sample);
    }

    public sealed record DecodeBatch(
        IReadOnlyList<Ft8Reception> Messages,
        int Candidates,
        int LdpcRejected,
        int CrcRejected,
        TimeSpan Duration);

    public DecodeBatch DecodeSlot(ReadOnlySpan<Complex32> samples, DateTimeOffset slotStart,
        Guid streamId, long channelCenterFrequencyHz, Ft8Settings settings,
        CancellationToken cancellationToken = default)
        => DecodeSlotCore(samples, slotStart, streamId, channelCenterFrequencyHz,
            settings, true, cancellationToken);

    private DecodeBatch DecodeSlotCore(ReadOnlySpan<Complex32> samples,
        DateTimeOffset slotStart, Guid streamId, long channelCenterFrequencyHz,
        Ft8Settings settings, bool allowCancellation,
        CancellationToken cancellationToken)
    {
        if (samples.Length < SlotSamples - SampleRateHz)
            return new([], 0, 0, 0, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        Waterfall waterfall = CreateWaterfall(samples);
        WsjtNoiseBaseline snrBaseline = CreateWsjtNoiseBaseline(samples);
        List<Candidate> candidates = FindCandidates(waterfall, settings);
        int candidateCount = candidates.Count;
        var decoded = new List<Ft8Reception>();
        var decodedLocations = new List<(string Payload, int AudioFrequency, double TimeOffset)>();
        int ldpcRejected = 0;
        int crcRejected = 0;
        float[] likelihoodBuffer = new float[Ft8Codec.CodewordBits];

        int fineCandidateCount = Math.Min(FineSynchronizationCandidateLimit,
            candidates.Count);
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            Candidate candidate = candidates[candidateIndex];
            cancellationToken.ThrowIfCancellationRequested();
            Span<float> likelihood = likelihoodBuffer;
            ExtractLikelihood(waterfall, candidate, likelihood);
            bool normalized = Normalize(likelihood);
            Ft8Codec.DecodedMessage? message = null;
            int parityErrors = int.MaxValue;
            bool crcValid = false;
            bool decodedMessage = normalized &&
                                  codec.TryDecode(likelihood, settings.LdpcIterations,
                                      out message, out parityErrors, out crcValid);
            double basebandOffset = (waterfall.MinimumBin + candidate.FrequencyOffset +
                                     candidate.FrequencySub / (double)FrequencyOversampling) * 6.25;
            double timeOffset = (candidate.TimeOffset +
                                 candidate.TimeSub / (double)TimeOversampling) * 0.160;

            bool fineDecoded = false;
            Ft8Codec.DecodedMessage? fineMessage = null;
            int fineParityErrors = int.MaxValue;
            bool fineCrcValid = false;
            double fineFrequency = basebandOffset;
            double fineTimeOffset = timeOffset;
            // A successful coarse decode is always refined to retain accurate
            // DT/DF reporting.  For unsuccessful candidates, only spend the
            // matched-filter cost on the highest-scoring hypotheses.
            if (enableFineSynchronization &&
                (decodedMessage || candidateIndex < fineCandidateCount))
                fineDecoded = TryFineDecode(samples, waterfall, candidate, likelihood,
                    settings.LdpcIterations, out fineMessage,
                    out fineParityErrors, out fineCrcValid,
                    out fineFrequency, out fineTimeOffset);
            if (fineDecoded || decodedMessage)
            {
                basebandOffset = fineFrequency;
                timeOffset = fineTimeOffset;
                if (fineDecoded)
                {
                    decodedMessage = true;
                    message = fineMessage;
                    parityErrors = fineParityErrors;
                    crcValid = fineCrcValid;
                }
            }
            if (!decodedMessage)
            {
                if (!normalized || parityErrors != 0) ldpcRejected++;
                else if (!crcValid) crcRejected++;
                continue;
            }
            int audioFrequency = (int)Math.Round(AudioCenterHz + basebandOffset);
            string fingerprint = Convert.ToHexString(message!.Payload);
            if (decodedLocations.Any(item =>
                    item.Payload == fingerprint &&
                    Math.Abs(item.AudioFrequency - audioFrequency) <= 75 &&
                    Math.Abs(item.TimeOffset - timeOffset) <= 1.0))
                continue;
            decodedLocations.Add((fingerprint, audioFrequency, timeOffset));

            int snr = CalculateSnr(samples, basebandOffset, timeOffset, message.Payload, snrBaseline);
            decoded.Add(new(
                slotStart,
                slotStart.AddSeconds(Math.Max(0, timeOffset)),
                streamId,
                channelCenterFrequencyHz + (long)Math.Round(basebandOffset),
                audioFrequency,
                timeOffset,
                snr,
                candidate.Score,
                message.Text,
                message.Type,
                message.FromCall,
                message.ToCall,
                message.Extra,
                message.Payload));
        }

        if (allowCancellation && enableCancellationPass && decoded.Count > 0)
        {
            Complex32[] residual = samples[..Math.Min(samples.Length, SlotSamples)].ToArray();
            foreach (Ft8Reception reception in decoded)
                CancelDecodedSignal(residual, reception, channelCenterFrequencyHz);
            DecodeBatch secondPass = DecodeSlotCore(residual, slotStart, streamId,
                channelCenterFrequencyHz, settings, false, cancellationToken);
            candidateCount += secondPass.Candidates;
            ldpcRejected += secondPass.LdpcRejected;
            crcRejected += secondPass.CrcRejected;
            foreach (Ft8Reception reception in secondPass.Messages)
            {
                string fingerprint = Convert.ToHexString(reception.Payload);
                if (decodedLocations.Any(item =>
                        item.Payload == fingerprint &&
                        Math.Abs(item.AudioFrequency - reception.AudioFrequencyHz) <= 75 &&
                        Math.Abs(item.TimeOffset - reception.TimeOffsetSeconds) <= 1.0))
                    continue;
                decodedLocations.Add((fingerprint, reception.AudioFrequencyHz,
                    reception.TimeOffsetSeconds));
                decoded.Add(reception);
            }
        }
        stopwatch.Stop();
        return new(decoded.OrderBy(item => item.AudioFrequencyHz).ToArray(),
            candidateCount, ldpcRejected, crcRejected, stopwatch.Elapsed);
    }

    private Waterfall CreateWaterfall(ReadOnlySpan<Complex32> samples)
    {
        int actualBlockCount = 1 + (Math.Min(samples.Length, SlotSamples) - FftSize) / HopSamples;
        int minimumBin = (int)Math.Floor((AudioMinimumHz - AudioCenterHz) * 0.160);
        int maximumBin = (int)Math.Ceiling((AudioMaximumHz - AudioCenterHz) * 0.160) + 1;
        int binCount = maximumBin - minimumBin;
        var magnitude = new byte[actualBlockCount * FrequencyOversampling * binCount];
        float scale = 2f / FftSize;

        for (int block = 0; block < actualBlockCount; block++)
        {
            int inputStart = block * HopSamples;
            for (int index = 0; index < FftSize; index++)
            {
                Complex32 sample = samples[inputStart + index];
                float weight = window[index] * scale;
                real[index] = sample.I * weight;
                imaginary[index] = sample.Q * weight;
            }
            fft.Transform(real, imaginary);
            int destination = block * FrequencyOversampling * binCount;
            for (int frequencySub = 0; frequencySub < FrequencyOversampling; frequencySub++)
            {
                for (int bin = minimumBin; bin < maximumBin; bin++)
                {
                    int fftBin = bin * FrequencyOversampling + frequencySub;
                    fftBin %= FftSize;
                    if (fftBin < 0) fftBin += FftSize;
                    float power = real[fftBin] * real[fftBin] + imaginary[fftBin] * imaginary[fftBin];
                    float db = 10 * MathF.Log10(MathF.Max(power, 1e-12f));
                    magnitude[destination++] = (byte)Math.Clamp((int)(2 * db + 240), 0, 255);
                }
            }
        }
        return new(magnitude, actualBlockCount, binCount, minimumBin);
    }

    private static List<Candidate> FindCandidates(Waterfall waterfall, Ft8Settings settings)
    {
        var candidates = new List<Candidate>(settings.MaximumCandidates * 2);
        for (int timeSub = 0; timeSub < TimeOversampling; timeSub++)
        for (int frequencySub = 0; frequencySub < FrequencyOversampling; frequencySub++)
        for (int timeOffset = -10; timeOffset < 20; timeOffset++)
        for (int frequencyOffset = 0; frequencyOffset + 7 < waterfall.BinCount; frequencyOffset++)
        {
            var candidate = new Candidate(0, timeOffset, frequencyOffset, timeSub, frequencySub);
            int score = SyncScore(waterfall, candidate);
            if (score >= settings.MinimumSyncScore)
                candidates.Add(candidate with { Score = score });
        }

        // Suppress the dense cluster of adjacent time/frequency hypotheses around
        // each physical signal while keeping differently timed overlapping signals.
        var selected = new List<Candidate>(settings.MaximumCandidates);
        foreach (Candidate candidate in candidates.OrderByDescending(item => item.Score))
        {
            if (selected.Any(item =>
                    Math.Abs(item.TimeOffset * TimeOversampling + item.TimeSub -
                             (candidate.TimeOffset * TimeOversampling + candidate.TimeSub)) <= 2 &&
                    Math.Abs(item.FrequencyOffset * FrequencyOversampling + item.FrequencySub -
                             (candidate.FrequencyOffset * FrequencyOversampling + candidate.FrequencySub)) <= 2))
                continue;
            selected.Add(candidate);
            if (selected.Count == settings.MaximumCandidates) break;
        }
        return selected;
    }

    private static int SyncScore(Waterfall waterfall, Candidate candidate)
    {
        int score = 0;
        int count = 0;
        for (int group = 0; group < 3; group++)
        for (int index = 0; index < 7; index++)
        {
            int relativeBlock = group * 36 + index;
            int block = candidate.TimeOffset + relativeBlock;
            if (block < 0 || block >= waterfall.BlockCount) continue;
            int tone = Costas[index];
            int current = waterfall.Get(block, candidate.TimeSub, candidate.FrequencySub,
                candidate.FrequencyOffset + tone);
            if (tone > 0)
            {
                score += current - waterfall.Get(block, candidate.TimeSub, candidate.FrequencySub,
                    candidate.FrequencyOffset + tone - 1);
                count++;
            }
            if (tone < 7)
            {
                score += current - waterfall.Get(block, candidate.TimeSub, candidate.FrequencySub,
                    candidate.FrequencyOffset + tone + 1);
                count++;
            }
            if (index > 0 && block > 0)
            {
                score += current - waterfall.Get(block - 1, candidate.TimeSub, candidate.FrequencySub,
                    candidate.FrequencyOffset + tone);
                count++;
            }
            if (index < 6 && block + 1 < waterfall.BlockCount)
            {
                score += current - waterfall.Get(block + 1, candidate.TimeSub, candidate.FrequencySub,
                    candidate.FrequencyOffset + tone);
                count++;
            }
        }
        return count == 0 ? int.MinValue : score / count;
    }

    private static void ExtractLikelihood(Waterfall waterfall, Candidate candidate, Span<float> likelihood)
    {
        Span<float> mapped = stackalloc float[8];
        for (int symbol = 0; symbol < 58; symbol++)
        {
            int channelSymbol = symbol + (symbol < 29 ? 7 : 14);
            int block = candidate.TimeOffset + channelSymbol;
            int bit = symbol * 3;
            if (block < 0 || block >= waterfall.BlockCount)
            {
                likelihood.Slice(bit, 3).Clear();
                continue;
            }
            for (int value = 0; value < 8; value++)
                mapped[value] = waterfall.Get(block, candidate.TimeSub, candidate.FrequencySub,
                    candidate.FrequencyOffset + Gray[value]) * 0.5f - 120;
            likelihood[bit] = Max4(mapped[4], mapped[5], mapped[6], mapped[7]) -
                              Max4(mapped[0], mapped[1], mapped[2], mapped[3]);
            likelihood[bit + 1] = Max4(mapped[2], mapped[3], mapped[6], mapped[7]) -
                                  Max4(mapped[0], mapped[1], mapped[4], mapped[5]);
            likelihood[bit + 2] = Max4(mapped[1], mapped[3], mapped[5], mapped[7]) -
                                  Max4(mapped[0], mapped[2], mapped[4], mapped[6]);
        }
    }

    private static bool Normalize(Span<float> likelihood)
    {
        double sum = 0;
        double sumSquared = 0;
        foreach (float value in likelihood)
        {
            sum += value;
            sumSquared += value * value;
        }
        double variance = (sumSquared - sum * sum / likelihood.Length) / likelihood.Length;
        if (variance < 1e-9 || double.IsNaN(variance)) return false;
        float factor = (float)Math.Sqrt(24 / variance);
        for (int index = 0; index < likelihood.Length; index++) likelihood[index] *= factor;
        return true;
    }
}
