namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class OuterPacketDecoder
{
    // Three packet boundaries (612 bytes) suffice to acquire byte alignment.
    // Drain incrementally so even a large input block needs no buffer growth.
    private readonly byte[] buffered = new byte[1024];
    private readonly byte[] rotated = new byte[204];
    private readonly byte[] decoded = new byte[204];
    private readonly byte[] transport = new byte[188];
    private readonly EnergyDescrambler descrambler = new();
    private readonly ReedSolomon204 reedSolomon = new();
    private int[] phaseStates = [];
    private int readOffset, bufferedCount;
    private bool synchronized;
    private int packetsPerFrame;
    private int packetInFrame = -1;
    private int consecutiveRejections;

    public long CorrectedPackets { get; private set; }
    public long RejectedPackets { get; private set; }

    public void Reset()
    {
        readOffset = bufferedCount = 0;
        synchronized = false;
        packetInFrame = -1;
        consecutiveRejections = 0;
        descrambler.ResetFrame();
        CorrectedPackets = RejectedPackets = 0;
    }

    public void ConfigureFramePackets(int count)
    {
        if (count == packetsPerFrame) return;
        packetsPerFrame = count;
        phaseStates = new int[Math.Max(0, count)];
        descrambler.ResetFrame();
        for (int phase = 0; phase < phaseStates.Length; phase++)
        {
            phaseStates[phase] = descrambler.State;
            descrambler.AdvancePackets(1);
        }
        packetInFrame = -1;
        consecutiveRejections = 0;
        descrambler.ResetFrame();
    }

    public IReadOnlyList<byte[]> Process(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<byte[]>();
        Process(bytes, packets.Add);
        return packets;
    }

    // Each delivered packet is independently owned; downstream async consumers
    // may retain it. Only intermediate buffers are reused.
    public void Process(ReadOnlySpan<byte> bytes, Action<byte[]> packetDecoded)
    {
        do
        {
            int count = Math.Min(bytes.Length, buffered.Length - bufferedCount);
            int writeOffset = (readOffset + bufferedCount) % buffered.Length;
            int first = Math.Min(count, buffered.Length - writeOffset);
            bytes[..first].CopyTo(buffered.AsSpan(writeOffset));
            bytes.Slice(first, count - first).CopyTo(buffered);
            bytes = bytes[count..];
            bufferedCount += count;
            Drain(packetDecoded);
        } while (!bytes.IsEmpty);
    }

    private void Drain(Action<byte[]> packetDecoded)
    {
        while (true)
        {
            if (!synchronized)
            {
                int start = FindPacketStart();
                if (start < 0)
                {
                    if (bufferedCount > 611) Consume(bufferedCount - 611);
                    return;
                }
                Consume(start);
                synchronized = true;
            }
            if (bufferedCount < 204) return;
            if (Peek(203) != 0x47)
            {
                synchronized = false;
                Consume(1);
                continue;
            }
            int first = Math.Min(204, buffered.Length - readOffset);
            buffered.AsSpan(readOffset, first).CopyTo(rotated);
            buffered.AsSpan(0, 204 - first).CopyTo(rotated.AsSpan(first));
            Consume(204);
            if (TryDecode())
            {
                CorrectedPackets++;
                packetDecoded(transport.ToArray());
            }
            else RejectedPackets++;
        }
    }

    private bool TryDecode()
    {
        if (packetsPerFrame <= 0) return false;
        if (packetInFrame < 0)
        {
            for (int phase = 0; phase < packetsPerFrame; phase++)
            {
                descrambler.State = phaseStates[phase];
                descrambler.ProcessPacket(rotated, decoded);
                if (!reedSolomon.TryCorrect(decoded, transport, out _)) continue;
                packetInFrame = (phase + 1) % packetsPerFrame;
                consecutiveRejections = 0;
                if (packetInFrame == 0) descrambler.ResetFrame();
                return true;
            }
            return false;
        }

        descrambler.ProcessPacket(rotated, decoded);
        packetInFrame = (packetInFrame + 1) % packetsPerFrame;
        if (packetInFrame == 0) descrambler.ResetFrame();
        if (reedSolomon.TryCorrect(decoded, transport, out _))
        {
            consecutiveRejections = 0;
            return true;
        }
        if (++consecutiveRejections >= 3)
        {
            packetInFrame = -1;
            consecutiveRejections = 0;
        }
        return false;
    }

    private byte Peek(int offset) => buffered[(readOffset + offset) % buffered.Length];
    private void Consume(int count)
    {
        readOffset = (readOffset + count) % buffered.Length;
        bufferedCount -= count;
    }

    private int FindPacketStart()
    {
        for (int start = 0; start + 611 < bufferedCount; start++)
            if (Peek(start + 203) == 0x47 && Peek(start + 407) == 0x47 &&
                Peek(start + 611) == 0x47) return start;
        return -1;
    }
}
