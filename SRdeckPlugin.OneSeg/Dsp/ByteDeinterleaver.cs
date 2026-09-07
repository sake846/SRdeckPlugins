namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class ByteDeinterleaver
{
    private readonly Queue<byte>[] branches = CreateBranches();
    private int initialBranch;
    private int branch;

    public void Reset()
        => Reset(initialBranch);

    public void Reset(int branchAtFirstByte)
    {
        if ((uint)branchAtFirstByte >= 12) throw new ArgumentOutOfRangeException(nameof(branchAtFirstByte));
        initialBranch = branchAtFirstByte;
        branch = branchAtFirstByte;
        for (int index = 0; index < branches.Length; index++)
        {
            branches[index].Clear();
            for (int delay = 0; delay < (11 - index) * 17; delay++) branches[index].Enqueue(0);
        }
    }

    public byte[] Process(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Process(input, output);
        return output;
    }

    public void Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        for (int index = 0; index < input.Length; index++)
        {
            Queue<byte> queue = branches[branch];
            queue.Enqueue(input[index]);
            output[index] = queue.Dequeue();
            branch = (branch + 1) % 12;
        }
    }

    private static Queue<byte>[] CreateBranches()
    {
        var result = new Queue<byte>[12];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new Queue<byte>((11 - index) * 17 + 1);
            for (int delay = 0; delay < (11 - index) * 17; delay++) result[index].Enqueue(0);
        }
        return result;
    }
}
