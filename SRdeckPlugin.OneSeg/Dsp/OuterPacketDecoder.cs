namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class OuterPacketDecoder
{
    private readonly List<byte> buffered = [];
    private EnergyDescrambler descrambler = new();
    private readonly ReedSolomon204 reedSolomon = new();
    private bool synchronized;
    private int packetsPerFrame;
    private int packetInFrame = -1;
    private int consecutiveRejections;

    public long CorrectedPackets { get; private set; }
    public long RejectedPackets { get; private set; }

    public void Reset()
    {
        buffered.Clear();
        synchronized = false;
        packetInFrame = -1;
        consecutiveRejections = 0;
        descrambler.ResetFrame();
        CorrectedPackets = 0;
        RejectedPackets = 0;
    }

    public void ConfigureFramePackets(int count)
    {
        if (count == packetsPerFrame) return;
        packetsPerFrame = count;
        packetInFrame = -1;
        consecutiveRejections = 0;
        descrambler.ResetFrame();
    }

    public IReadOnlyList<byte[]> Process(ReadOnlySpan<byte> bytes)
    {
        buffered.AddRange(bytes.ToArray());
        var packets = new List<byte[]>();
        while (true)
        {
            if (!synchronized)
            {
                int start = FindPacketStart();
                if (start < 0)
                {
                    if (buffered.Count > 611) buffered.RemoveRange(0, buffered.Count - 611);
                    break;
                }
                if (start > 0) buffered.RemoveRange(0, start);
                synchronized = true;
            }
            if (buffered.Count < 204) break;
            if (buffered[203] != 0x47)
            {
                synchronized = false;
                buffered.RemoveAt(0);
                continue;
            }
            byte[] rotated = buffered.GetRange(0, 204).ToArray();
            buffered.RemoveRange(0, 204);
            if (TryDecode(rotated, out byte[] transport))
            {
                CorrectedPackets++;
                packets.Add(transport);
            }
            else
            {
                RejectedPackets++;
            }
        }
        return packets;
    }

    private bool TryDecode(byte[] rotated, out byte[] transport)
    {
        transport = [];
        if (packetsPerFrame <= 0) return false;
        if (packetInFrame < 0)
        {
            for (int phase = 0; phase < packetsPerFrame; phase++)
            {
                var candidate = new EnergyDescrambler();
                candidate.AdvancePackets(phase);
                byte[] outer = candidate.ProcessPacket(rotated);
                if (!reedSolomon.TryCorrect(outer, out transport, out _)) continue;
                descrambler = candidate;
                packetInFrame = (phase + 1) % packetsPerFrame;
                consecutiveRejections = 0;
                if (packetInFrame == 0) descrambler.ResetFrame();
                return true;
            }
            return false;
        }

        byte[] decoded = descrambler.ProcessPacket(rotated);
        packetInFrame = (packetInFrame + 1) % packetsPerFrame;
        if (packetInFrame == 0) descrambler.ResetFrame();
        if (reedSolomon.TryCorrect(decoded, out transport, out _))
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

    private int FindPacketStart()
    {
        // A single random 0x47 occurs often enough in undecoded data to trigger an
        // expensive Reed-Solomon phase search.  Require three consecutive 204-byte
        // packet boundaries before declaring byte synchronization.
        for (int start = 0; start + 611 < buffered.Count; start++)
        {
            if (buffered[start + 203] == 0x47 && buffered[start + 407] == 0x47 &&
                buffered[start + 611] == 0x47) return start;
        }
        return -1;
    }
}
