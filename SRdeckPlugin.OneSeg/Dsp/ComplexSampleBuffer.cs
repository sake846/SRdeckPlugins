using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class ComplexSampleBuffer
{
    private Complex32[] samples = new Complex32[131_072];
    private int offset;

    public int Count { get; private set; }
    public ReadOnlySpan<Complex32> Span => samples.AsSpan(offset, Count);

    public void Append(ReadOnlySpan<Complex32> source)
    {
        EnsureCapacity(source.Length);
        source.CopyTo(samples.AsSpan(offset + Count));
        Count += source.Length;
    }

    public void Consume(int count)
    {
        if ((uint)count > (uint)Count) throw new ArgumentOutOfRangeException(nameof(count));
        offset += count;
        Count -= count;
        if (Count == 0) offset = 0;
        else if (offset > samples.Length / 2)
        {
            samples.AsSpan(offset, Count).CopyTo(samples);
            offset = 0;
        }
    }

    public void Clear()
    {
        offset = 0;
        Count = 0;
    }

    private void EnsureCapacity(int additional)
    {
        if (additional <= samples.Length - offset - Count) return;
        if (additional <= samples.Length - Count)
        {
            samples.AsSpan(offset, Count).CopyTo(samples);
            offset = 0;
            return;
        }
        int required = checked(Count + additional);
        int capacity = samples.Length;
        while (capacity < required) capacity = checked(capacity * 2);
        var expanded = new Complex32[capacity];
        samples.AsSpan(offset, Count).CopyTo(expanded);
        samples = expanded;
        offset = 0;
    }
}
