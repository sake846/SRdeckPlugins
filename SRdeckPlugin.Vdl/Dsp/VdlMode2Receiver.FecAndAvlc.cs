using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    private long CreateRecoveryDeadline() => Stopwatch.GetTimestamp() + Stopwatch.Frequency *
        Math.Max(0, RecoveryBudgetMilliseconds) / 1_000;

    private static bool IsRecoveryDeadlineExceeded(long deadline) =>
        Stopwatch.GetTimestamp() >= deadline;

    private static IEnumerable<int[]> EnumerateCombinations(int[] values, int count)
    {
        if (count <= 0 || count > values.Length) yield break;
        int[] indices = Enumerable.Range(0, count).ToArray();
        while (true)
        {
            var result = new int[count];
            for (int index = 0; index < count; index++) result[index] = values[indices[index]];
            yield return result;
            int pivot = count - 1;
            while (pivot >= 0 && indices[pivot] == values.Length - count + pivot) pivot--;
            if (pivot < 0) yield break;
            indices[pivot]++;
            for (int index = pivot + 1; index < count; index++)
                indices[index] = indices[index - 1] + 1;
        }
    }

    private void DemodulateHypothesis(IReadOnlyList<Complex32> symbols,
        double frequencyOffsetHz, out List<bool> bits, out List<double> reliabilities)
    {
        bits = new(symbols.Count * 3);
        reliabilities = new(symbols.Count * 3);
        double previous = burstInitialPreviousPhase;
        double drift = burstInitialPhaseDrift + frequencyOffsetHz * 2 * Math.PI / SymbolRate;
        foreach (Complex32 symbol in symbols)
        {
            double phase = Math.Atan2(symbol.Q, symbol.I);
            double difference = WrapPositive(phase - previous - drift);
            previous = phase;
            int phaseIndex = ((int)Math.Round(difference / (Math.PI / 4))) & 7;
            double error = WrapSigned(difference - phaseIndex * Math.PI / 4);
            drift = Math.Clamp(drift + CarrierTrackingLoopGain * error,
                -MaximumCarrierCorrection, MaximumCarrierCorrection);
            int decoded = GrayCode[phaseIndex];
            double reliability = Math.Clamp(1 - Math.Abs(error) / (Math.PI / 8), 0, 1);
            bits.Add((decoded & 4) != 0);
            bits.Add((decoded & 2) != 0);
            bits.Add((decoded & 1) != 0);
            reliabilities.Add(reliability);
            reliabilities.Add(reliability);
            reliabilities.Add(reliability);
        }
    }

    private static bool TryDeinterleave(byte[] input, int rows, int fillWidth,
        int offset, byte[][] output)
    {
        if (rows <= 0 || fillWidth <= 0 || output.Length < rows) return false;
        int lastRowLength = input.Length % fillWidth;
        if (lastRowLength == 0) lastRowLength = fillWidth;
        if (input.Length > rows * fillWidth ||
            (rows > 1 && input.Length - lastRowLength < (rows - 1) * fillWidth)) return false;
        int row = 0;
        int column = offset;
        int lastColumn = lastRowLength + offset;
        foreach (byte value in input)
        {
            if (row == rows - 1 && column >= lastColumn)
            {
                row = 0;
                column++;
            }
            output[row++][column] = value;
            if (row == rows) { row = 0; column++; }
        }
        return true;
    }

    private static bool TryDeinterleave(double[] input, int rows, int fillWidth,
        int offset, double[][] output)
    {
        if (rows <= 0 || fillWidth <= 0 || output.Length < rows) return false;
        int lastRowLength = input.Length % fillWidth;
        if (lastRowLength == 0) lastRowLength = fillWidth;
        if (input.Length > rows * fillWidth ||
            (rows > 1 && input.Length - lastRowLength < (rows - 1) * fillWidth)) return false;
        int row = 0;
        int column = offset;
        int lastColumn = lastRowLength + offset;
        foreach (double value in input)
        {
            if (row == rows - 1 && column >= lastColumn)
            {
                row = 0;
                column++;
            }
            output[row++][column] = value;
            if (row == rows) { row = 0; column++; }
        }
        return true;
    }

    private void ExtractAvlcFrames(IReadOnlyList<bool> bits, IqBlockMetadata metadata,
        long frequencyHz, List<VdlFrame> output, bool countDiagnostics = true)
    {
        int search = 0;
        while (true)
        {
            int start = FindFlag(bits, search);
            if (start < 0) return;
            int end = FindFlag(bits, start + 8);
            if (end < 0) return;
            if (countDiagnostics) AvlcFlagPairCount++;
            if (TryUnstuff(bits, start + 8, end, out List<bool>? payloadBits) &&
                payloadBits.Count >= 24 && payloadBits.Count % 8 == 0)
            {
                if (countDiagnostics) AvlcUnstuffedFrameCount++;
                byte[] frame = PackLsb(payloadBits, 0, payloadBits.Count / 8);
                if (HasValidFcs(frame))
                {
                    long samplePosition = metadata.AbsoluteSampleStart + (long)Math.Round(
                        start / 3.0 * metadata.SampleRateHz / SymbolRate);
                    output.Add(new(frame[..^2], metadata.UtcTimestamp, frequencyHz,
                        metadata.StreamId, samplePosition));
                }
                else if (countDiagnostics) AvlcFcsRejectedFrameCount++;
            }
            search = end;
        }
    }

    private static bool TryUnstuff(IReadOnlyList<bool> source, int start, int end,
        out List<bool> output)
    {
        output = [];
        int ones = 0;
        for (int i = start; i < end; i++)
        {
            bool bit = source[i];
            if (bit)
            {
                if (++ones > 5) return false;
                output.Add(true);
            }
            else
            {
                if (ones == 5) { ones = 0; continue; }
                output.Add(false);
                ones = 0;
            }
        }
        return true;
    }

    private static int FindFlag(IReadOnlyList<bool> bits, int offset)
    {
        for (int i = Math.Max(0, offset); i + 8 <= bits.Count; i++)
            if (!bits[i] && bits[i + 1] && bits[i + 2] && bits[i + 3] &&
                bits[i + 4] && bits[i + 5] && bits[i + 6] && !bits[i + 7])
                return i;
        return -1;
    }

    private static void Descramble(bool[] bits, int count)
    {
        ushort lfsr = ScramblerInitialValue;
        for (int i = 0; i < count; i++)
        {
            bool feedback = (((lfsr >> 0) ^ (lfsr >> 14)) & 1) != 0;
            lfsr = (ushort)((lfsr >> 1) | (feedback ? 1 << 14 : 0));
            bits[i] ^= feedback;
        }
    }

    private static uint ReadMsbWord(IReadOnlyList<bool> bits, int offset, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++) value = (value << 1) | (bits[offset + i] ? 1u : 0u);
        return value;
    }

    private static byte[] PackLsb(IReadOnlyList<bool> bits, int offset, int byteCount)
    {
        var bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                int index = offset + i * 8 + bit;
                if (index < bits.Count && bits[index]) bytes[i] |= (byte)(1 << bit);
            }
        return bytes;
    }

    private static double[] PackReliability(IReadOnlyList<double> reliability,
        int offset, int byteCount)
    {
        var result = new double[byteCount];
        for (int index = 0; index < byteCount; index++)
        {
            double minimum = 1;
            for (int bit = 0; bit < 8; bit++)
            {
                int source = offset + index * 8 + bit;
                if (source < reliability.Count) minimum = Math.Min(minimum, reliability[source]);
            }
            result[index] = minimum;
        }
        return result;
    }

    private static int HeaderSyndrome(uint header)
    {
        int syndrome = 0;
        for (int row = 0; row < HeaderParityChecks.Length; row++)
            syndrome |= (System.Numerics.BitOperations.PopCount(header & HeaderParityChecks[row]) & 1)
                        << (HeaderFecBits - 1 - row);
        return syndrome;
    }

    private static uint ReverseBits(uint value, int count)
    {
        uint result = 0;
        for (int i = 0; i < count; i++) result = (result << 1) | ((value >> i) & 1);
        return result;
    }

    private static int GetFecOctetCount(int length) => length switch
    {
        < 3 => 0,
        < 31 => 2,
        < 68 => 4,
        _ => 6
    };

    internal static int SelectCoarseDecimationFactor(int sampleRateHz)
        => VdlInputPreparationStage.SelectCoarseDecimationFactor(
            sampleRateHz,
            WorkingSampleRate,
            maximumSampleRateHz: 240_000);

    internal static float[] CreateRootRaisedCosineTaps()
    {
        const double rollOff = 0.6;
        const int symbolSpan = 8;
        int length = symbolSpan * SamplesPerSymbol + 1;
        var result = new float[length];
        int center = length / 2;
        double sum = 0;
        for (int index = 0; index < length; index++)
        {
            double time = (index - center) / (double)SamplesPerSymbol;
            double value;
            if (Math.Abs(time) < 1e-12)
                value = 1 + rollOff * (4 / Math.PI - 1);
            else if (Math.Abs(Math.Abs(4 * rollOff * time) - 1) < 1e-9)
            {
                double angle = Math.PI / (4 * rollOff);
                value = rollOff / Math.Sqrt(2) *
                    ((1 + 2 / Math.PI) * Math.Sin(angle) +
                     (1 - 2 / Math.PI) * Math.Cos(angle));
            }
            else
            {
                double numerator = Math.Sin(Math.PI * time * (1 - rollOff)) +
                                   4 * rollOff * time * Math.Cos(Math.PI * time * (1 + rollOff));
                double denominator = Math.PI * time * (1 - Math.Pow(4 * rollOff * time, 2));
                value = numerator / denominator;
            }
            result[index] = (float)value;
            sum += value;
        }
        for (int index = 0; index < result.Length; index++) result[index] /= (float)sum;
        return result;
    }

    private static double WrapPositive(double phase)
    {
        phase %= 2 * Math.PI;
        return phase < 0 ? phase + 2 * Math.PI : phase;
    }

    private static double WrapSigned(double phase)
    {
        phase = WrapPositive(phase);
        return phase > Math.PI ? phase - 2 * Math.PI : phase;
    }

    private void RejectBurst()
    {
        RejectedFrameCount++;
        ResetBurst();
    }

    private void ResetBurst()
    {
        state = ReceiverState.Searching;
        syncWriteIndex = 0;
        syncSampleCount = 0;
        Array.Clear(syncBuffer);
        burstBits.Clear();
        burstReliabilities.Clear();
        burstSymbols.Clear();
        burstSymbolsEarly.Clear();
        burstSymbolsLate.Clear();
        ResetBurstQuality();
        adaptiveEqualizerConfigured = false;
        adaptiveEqualizerPrevious1 = Complex.Zero;
        adaptiveEqualizerPrevious2 = Complex.Zero;
        requestedBurstBits = HeaderLength;
        transmissionLength = 0;
        timingErrorPending = false;
        timingRateCorrection = 0;
        previousSyncError = double.PositiveInfinity;
    }

    private void ResetBurstQuality()
    {
        burstCarrierErrorPower = 0;
        burstCarrierUpdateCount = 0;
        burstTimingErrorPower = 0;
        burstTimingUpdateCount = 0;
    }

    private static bool HasValidFcs(ReadOnlySpan<byte> frame)
    {
        ushort crc = 0xffff;
        foreach (byte value in frame)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return crc == 0xf0b8;
    }
}
