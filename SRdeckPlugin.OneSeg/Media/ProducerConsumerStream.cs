using System.Collections.Concurrent;
using System.IO;

namespace SRdeckPlugin.OneSeg.Media;

internal sealed class ProducerConsumerStream : Stream
{
    // One-seg transport data arrives in short bursts as IQ blocks are decoded.
    // Keep several seconds of headroom so a temporary LibVLC read stall does not
    // turn into missing AAC packets. The consumer does not wait for this buffer
    // to fill, so the larger bound does not add playback latency.
    private const int QueueCapacityPackets = 4096;
    private readonly BlockingCollection<byte[]> queue =
        new(new ConcurrentQueue<byte[]>(), QueueCapacityPackets);
    private byte[]? current;
    private int currentOffset;
    private int disposed;

    public void WritePacket(ReadOnlySpan<byte> packet)
    {
        if (disposed != 0 || queue.IsAddingCompleted) return;
        // LibVLC can temporarily stop reading while it is being stopped or
        // while it is waiting for a decodable TS.  A blocking TryAdd here
        // would then hold the SDR processing worker forever and prevent the
        // application from shutting down.  This is a live stream, so dropping
        // a packet is preferable to blocking the receiver; the next packets
        // will resynchronize the MPEG-TS decoder.
        try { queue.TryAdd(packet.ToArray(), 0); }
        catch (InvalidOperationException) { }
    }

    public void Complete()
    {
        if (!queue.IsAddingCompleted) queue.CompleteAdding();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;

        int total = 0;
        while (total < buffer.Length)
        {
            while (current is null || currentOffset >= current.Length)
            {
                try
                {
                    // Block only until the first packet is available. Once data
                    // has been copied, drain everything already queued into the
                    // same native read callback instead of returning 188 bytes at
                    // a time and allowing the producer queue to grow needlessly.
                    if (total == 0) current = queue.Take();
                    else if (!queue.TryTake(out current)) return total;
                }
                catch (InvalidOperationException) { return total; }
                currentOffset = 0;
            }

            int copied = Math.Min(buffer.Length - total, current.Length - currentOffset);
            current.AsSpan(currentOffset, copied).CopyTo(buffer[total..]);
            currentOffset += copied;
            total += copied;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Complete();
            queue.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
