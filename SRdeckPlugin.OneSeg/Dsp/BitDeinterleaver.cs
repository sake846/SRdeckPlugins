using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class BitDeinterleaver
{
    private static readonly int[] QpskDelays = [120, 0];
    private static readonly int[] Qam16Delays = [120, 80, 40, 0];
    private static readonly int[] Qam64Delays = [120, 96, 72, 48, 24, 0];
    private Queue<sbyte>[] queues = [];
    private IsdbTModulation modulation = IsdbTModulation.Unused;

    public void Reset()
    {
        queues = [];
        modulation = IsdbTModulation.Unused;
    }

    public byte[] Process(ReadOnlySpan<byte> bits, IsdbTModulation current)
    {
        var output = new byte[bits.Length];
        Process(bits, current, output);
        return output;
    }

    public void Process(ReadOnlySpan<byte> bits, IsdbTModulation current, Span<byte> output)
    {
        EnsureQueues(current);
        if (bits.Length % queues.Length != 0)
            throw new ArgumentException("The constellation bit count is incomplete.", nameof(bits));
        if (output.Length < bits.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        for (int symbol = 0; symbol < bits.Length / queues.Length; symbol++)
        for (int bit = 0; bit < queues.Length; bit++)
        {
            Queue<sbyte> queue = queues[bit];
            byte value = bits[symbol * queues.Length + bit];
            queue.Enqueue(value == 0 ? (sbyte)-127 : value == 1 ? (sbyte)127 : (sbyte)0);
            output[symbol * queues.Length + bit] = queue.Dequeue() > 0 ? (byte)1 : (byte)0;
        }
    }

    public void ProcessSoft(
        ReadOnlySpan<sbyte> reliabilities,
        IsdbTModulation current,
        Span<sbyte> output)
    {
        EnsureQueues(current);
        if (reliabilities.Length % queues.Length != 0)
            throw new ArgumentException("The constellation bit count is incomplete.",
                nameof(reliabilities));
        if (output.Length < reliabilities.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        for (int symbol = 0; symbol < reliabilities.Length / queues.Length; symbol++)
        for (int bit = 0; bit < queues.Length; bit++)
        {
            Queue<sbyte> queue = queues[bit];
            queue.Enqueue(reliabilities[symbol * queues.Length + bit]);
            output[symbol * queues.Length + bit] = queue.Dequeue();
        }
    }

    private void EnsureQueues(IsdbTModulation current)
    {
        int[] delays = current switch
        {
            // SymbolDemapper emits each constellation word MSB first.  ARIB's
            // delay tables are indexed LSB first, so reverse them here.
            IsdbTModulation.Dqpsk or IsdbTModulation.Qpsk => QpskDelays,
            IsdbTModulation.Qam16 => Qam16Delays,
            IsdbTModulation.Qam64 => Qam64Delays,
            _ => throw new NotSupportedException($"Unsupported modulation {current}.")
        };
        if (modulation == current) return;
        modulation = current;
        queues = delays.Select(delay =>
        {
            var queue = new Queue<sbyte>(delay + 1);
            for (int index = 0; index < delay; index++) queue.Enqueue(0);
            return queue;
        }).ToArray();
    }
}
