namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class EnergyDescrambler
{
    private int register = 0x00a9;

    public void ResetFrame() => register = 0x00a9;

    public void AdvancePackets(int count)
    {
        for (int packet = 0; packet < count; packet++)
            for (int index = 0; index < 204; index++) _ = NextByte();
    }

    public byte[] ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 204) throw new ArgumentException("An ISDB-T outer packet is 204 bytes.", nameof(packet));
        var output = new byte[204];
        output[0] = packet[203];
        for (int index = 1; index < output.Length; index++)
            output[index] = (byte)(packet[index - 1] ^ NextByte());
        _ = NextByte();
        return output;
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
