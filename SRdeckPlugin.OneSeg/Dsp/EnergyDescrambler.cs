namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class EnergyDescrambler
{
    private int register = 0x00a9;

    internal int State { get => register; set => register = value; }

    public void ResetFrame() => register = 0x00a9;

    public void AdvancePackets(int count)
    {
        for (int packet = 0; packet < count; packet++)
            for (int index = 0; index < 204; index++) _ = NextByte();
    }

    public byte[] ProcessPacket(ReadOnlySpan<byte> packet)
    {
        var output = new byte[204];
        ProcessPacket(packet, output);
        return output;
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet, Span<byte> output)
    {
        if (packet.Length != 204) throw new ArgumentException("An ISDB-T outer packet is 204 bytes.", nameof(packet));
        if (output.Length < 204) throw new ArgumentException("The output must hold 204 bytes.", nameof(output));
        output[0] = packet[203];
        for (int index = 1; index < 204; index++)
            output[index] = (byte)(packet[index - 1] ^ NextByte());
        _ = NextByte();
    }

    private byte NextByte()
    {
        byte value = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            int feedback = ((register >> 13) ^ (register >> 14)) & 1;
            register = ((register << 1) | feedback) & 0x7fff;
            value = (byte)((value << 1) | feedback);
        }
        return value;
    }
}
