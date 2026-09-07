using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class TimeDeinterleaver
{
    private Queue<ComplexF>[] queues = [];
    private IsdbTMode mode;
    private int interleavingLength = -1;

    public void Reset()
    {
        queues = [];
        mode = IsdbTMode.Unknown;
        interleavingLength = -1;
    }

    public ComplexF[] Process(ReadOnlySpan<ComplexF> input, IsdbTMode currentMode, int length)
    {
        var output = new ComplexF[input.Length];
        Process(input, currentMode, length, output);
        return output;
    }

    public void Process(ReadOnlySpan<ComplexF> input, IsdbTMode currentMode, int length,
        Span<ComplexF> output)
    {
        if (queues.Length != input.Length || mode != currentMode || interleavingLength != length)
            Configure(currentMode, length, input.Length);
        if (output.Length < input.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        for (int carrier = 0; carrier < input.Length; carrier++)
        {
            Queue<ComplexF> queue = queues[carrier];
            queue.Enqueue(input[carrier]);
            output[carrier] = queue.Dequeue();
        }
    }

    private void Configure(IsdbTMode currentMode, int length, int carriers)
    {
        mode = currentMode;
        interleavingLength = Math.Max(0, length);
        queues = new Queue<ComplexF>[carriers];
        for (int carrier = 0; carrier < carriers; carrier++)
        {
            int delay = interleavingLength * (95 - (5 * carrier) % 96);
            var queue = new Queue<ComplexF>(delay + 1);
            for (int index = 0; index < delay; index++) queue.Enqueue(default);
            queues[carrier] = queue;
        }
    }
}
