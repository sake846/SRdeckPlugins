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
        ReadOnlySpan<int> branchMetrics =
        [
            2 * MaximumReliability + x + y, 2 * MaximumReliability + x - y,
            2 * MaximumReliability - x + y, 2 * MaximumReliability - x - y
        ];
        ulong decisionMask = 0;
        // Both generators contain the newest and oldest bits. Flipping either
        // complements both encoded bits, so a butterfly shares two branch costs.
        for (int predecessor = 0; predecessor < StateCount / 2; predecessor++)
        {
            int next = predecessor * 2;
            int branch0 = branchMetrics[TransitionOutputs[predecessor]];
            int branch1 = 4 * MaximumReliability - branch0;
            int metric0 = metrics[predecessor], metric1 = metrics[predecessor + StateCount / 2];
            int even0 = metric0 + branch0, even1 = metric1 + branch1;
            int odd0 = metric0 + branch1, odd1 = metric1 + branch0;
            nextMetrics[next] = Math.Min(even0, even1);
            nextMetrics[next + 1] = Math.Min(odd0, odd1);
            decisionMask |= (even1 < even0 ? 1UL : 0UL) << next;
            decisionMask |= (odd1 < odd0 ? 1UL : 0UL) << (next + 1);
        }
        (metrics, nextMetrics) = (nextMetrics, metrics);
        decisions[steps % DecisionDepth] = decisionMask;
        steps++;
        bool hasOutput = steps >= DecisionDepth && (steps - DecisionDepth) % OutputBits == 0;
        bool normalize = (steps & 0xff) == 0;
        int bestState = 0;
        int bestMetric = Infinity;
        // The winning state is only needed for byte traceback or normalization.
        if (hasOutput || normalize)
        {
            for (int state = 0; state < StateCount; state++)
            {
                if (metrics[state] >= bestMetric) continue;
                bestMetric = metrics[state];
                bestState = state;
            }
        }
        output = hasOutput ? TracebackByte(bestState) : default;
        if (normalize)
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

    private static int HardReliability(byte bit) => bit switch
    {
        0 => -MaximumReliability,
        1 => MaximumReliability,
        _ => 0
    };

    private static byte[] CreateTransitionOutputs()
    {
        var result = new byte[StateCount / 2];
        for (int state = 0; state < result.Length; state++)
        {
            int shift = state << 1;
            // ISDB-T K=7 generators in the decoder's LSB-newest shift order.
            result[state] = (byte)((Parity(shift & 0x4f) << 1) |
                Parity(shift & 0x6d));
        }
        return result;
    }

    private static int Parity(int value) => System.Numerics.BitOperations.PopCount((uint)value) & 1;
}
