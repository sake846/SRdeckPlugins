using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class ViterbiDecoder
{
    private const int StateCount = 64;
    private const int Traceback = 48;
    private const int OutputBits = 8;
    private const int DecisionDepth = Traceback + OutputBits;
    private const int MaximumReliability = SymbolDemapper.MaximumReliability;
    private const int Infinity = 1_000_000;
    private static readonly byte[] TransitionOutputs = CreateTransitionOutputs();
    private int[] metrics = new int[StateCount];
    private int[] nextMetrics = new int[StateCount];
    private readonly ulong[] decisions = new ulong[DecisionDepth];
    private byte[] pattern = [1,1];
    private int patternIndex, motherPosition, receivedX, receivedY, steps;

    public ViterbiDecoder() => Reset(IsdbTCodeRate.Rate1Over2);

    public void Reset(IsdbTCodeRate rate)
    {
        pattern = rate switch
        {
            IsdbTCodeRate.Rate1Over2 => [1,1],
            IsdbTCodeRate.Rate2Over3 => [1,1,0,1],
            IsdbTCodeRate.Rate3Over4 => [1,1,0,1,1,0],
            IsdbTCodeRate.Rate5Over6 => [1,1,0,1,1,0,0,1,1,0],
            IsdbTCodeRate.Rate7Over8 => [1,1,0,1,0,1,0,1,1,0,0,1,1,0],
            _ => throw new NotSupportedException($"Unsupported code rate {rate}.")
        };
        Array.Fill(metrics, Infinity);
        metrics[0] = 0;
        Array.Clear(decisions);
        patternIndex = motherPosition = steps = 0;
        receivedX = receivedY = 0;
    }

    public byte[] Process(ReadOnlySpan<byte> puncturedBits)
    {
        var workspace = new byte[MaximumOutputBytes(puncturedBits.Length)];
        int count = Process(puncturedBits, workspace);
        return workspace.AsSpan(0, count).ToArray();
    }

    public int Process(ReadOnlySpan<byte> puncturedBits, Span<byte> output)
    {
        if (output.Length < MaximumOutputBytes(puncturedBits.Length))
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        int outputCount = 0;
        int input = 0;
        while (input < puncturedBits.Length)
        {
            SetMotherBit(pattern[patternIndex] == 0
                ? 0
                : HardReliability(puncturedBits[input++]));
            patternIndex = (patternIndex + 1) % pattern.Length;
            if (motherPosition != 2) continue;
            if (DecodeStep(receivedX, receivedY, out byte decoded))
                output[outputCount++] = decoded;
            motherPosition = 0;
            receivedX = receivedY = 0;
        }
        return outputCount;
    }

    public int ProcessSoft(ReadOnlySpan<sbyte> puncturedReliabilities, Span<byte> output)
    {
        if (output.Length < MaximumOutputBytes(puncturedReliabilities.Length))
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        int outputCount = 0;
        int input = 0;
        while (input < puncturedReliabilities.Length)
        {
            SetMotherBit(pattern[patternIndex] == 0
                ? 0
                : puncturedReliabilities[input++]);
            patternIndex = (patternIndex + 1) % pattern.Length;
            if (motherPosition != 2) continue;
            if (DecodeStep(receivedX, receivedY, out byte decoded))
                output[outputCount++] = decoded;
            motherPosition = 0;
            receivedX = receivedY = 0;
        }
        return outputCount;
    }

    internal static int MaximumOutputBytes(int puncturedBitCount) => puncturedBitCount / 8 + 2;

    private void SetMotherBit(int bit)
    {
        if (motherPosition == 0) receivedX = bit; else receivedY = bit;
        motherPosition++;
    }

    private bool DecodeStep(int x, int y, out byte output)
    {
        int bestState = 0;
        int bestMetric = Infinity;
        ulong decisionMask = 0;
        for (int next = 0; next < StateCount; next++)
        {
            int bit = next & 1;
            int predecessor0 = next >> 1;
            int predecessor1 = predecessor0 | 0x20;
            int candidate0 = metrics[predecessor0] +
                BranchMetric(x, y, TransitionOutputs[(predecessor0 << 1) | bit]);
            int candidate1 = metrics[predecessor1] +
                BranchMetric(x, y, TransitionOutputs[(predecessor1 << 1) | bit]);
            if (candidate0 <= candidate1)
            {
                nextMetrics[next] = candidate0;
            }
            else
            {
                nextMetrics[next] = candidate1;
                decisionMask |= 1UL << next;
            }
            if (nextMetrics[next] >= bestMetric) continue;
            bestMetric = nextMetrics[next];
            bestState = next;
        }
        (metrics, nextMetrics) = (nextMetrics, metrics);
        decisions[steps % DecisionDepth] = decisionMask;
        steps++;
        bool hasOutput = steps >= DecisionDepth && (steps - DecisionDepth) % OutputBits == 0;
        output = hasOutput ? TracebackByte(bestState) : default;
        if ((steps & 0xff) == 0)
        {
            for (int state = 0; state < StateCount; state++) metrics[state] -= bestMetric;
        }
        return hasOutput;
    }

    private byte TracebackByte(int state)
    {
        byte decoded = 0;
        for (int depth = 0; depth < DecisionDepth; depth++)
        {
            int bit = state & 1;
            if (depth >= Traceback) decoded = (byte)((decoded >> 1) | (bit << 7));
            int decisionIndex = (steps - 1 - depth) % DecisionDepth;
            int predecessorHighBit = (int)((decisions[decisionIndex] >> state) & 1);
            state = (state >> 1) | (predecessorHighBit << 5);
        }
        return decoded;
    }

    private static int BranchMetric(int x, int y, int encoded) =>
        BitMetric(x, encoded >> 1) + BitMetric(y, encoded & 1);

    private static int BitMetric(int reliability, int expected) =>
        MaximumReliability + (expected == 0 ? reliability : -reliability);

    private static int HardReliability(byte bit) => bit switch
    {
        0 => -MaximumReliability,
        1 => MaximumReliability,
        _ => 0
    };

    private static byte[] CreateTransitionOutputs()
    {
        var result = new byte[StateCount * 2];
        for (int state = 0; state < StateCount; state++)
        for (int bit = 0; bit <= 1; bit++)
        {
            int shift = (state << 1) | bit;
            // ISDB-T K=7 generators in the decoder's LSB-newest shift order.
            result[(state << 1) | bit] = (byte)((Parity(shift & 0x4f) << 1) |
                Parity(shift & 0x6d));
        }
        return result;
    }

    private static int Parity(int value) => System.Numerics.BitOperations.PopCount((uint)value) & 1;
}
