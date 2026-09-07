namespace SRdeckPlugin.OneSeg.Media;

/// <summary>
/// Supplies the PAT omitted from an ISDB-T partial-reception segment so a
/// generic MPEG-TS demultiplexer can associate its video and audio with the
/// fixed one-seg PMT PID 0x1FC8.
/// </summary>
internal sealed class OneSegPatInjector
{
    private int continuityCounter;

    public void Reset() => continuityCounter = 0;

    public bool TryCreateForPmt(ReadOnlySpan<byte> packet, out byte[] pat)
    {
        pat = [];
        if (packet.Length != 188 || packet[0] != 0x47 ||
            (packet[1] & 0x40) == 0 || (((packet[1] & 0x1f) << 8) | packet[2]) != 0x1fc8)
            return false;

        int adaptation = (packet[3] >> 4) & 3;
        if (adaptation is 0 or 2) return false;
        int payload = 4;
        if (adaptation == 3)
        {
            if (payload >= packet.Length) return false;
            payload += 1 + packet[payload];
        }
        if (payload >= packet.Length) return false;
        int section = payload + 1 + packet[payload];
        if (section + 5 > packet.Length || packet[section] != 0x02) return false;

        ushort serviceId = (ushort)((packet[section + 3] << 8) | packet[section + 4]);
        pat = CreatePat(serviceId);
        return true;
    }

    private byte[] CreatePat(ushort serviceId)
    {
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xff);
        packet[0] = 0x47;
        packet[1] = 0x40;
        packet[2] = 0x00;
        packet[3] = (byte)(0x10 | continuityCounter);
        continuityCounter = (continuityCounter + 1) & 0x0f;
        packet[4] = 0;

        Span<byte> section = packet.AsSpan(5, 16);
        section[0] = 0x00;
        section[1] = 0xb0;
        section[2] = 0x0d;
        section[3] = (byte)(serviceId >> 8);
        section[4] = (byte)serviceId;
        section[5] = 0xc1;
        section[6] = 0;
        section[7] = 0;
        section[8] = (byte)(serviceId >> 8);
        section[9] = (byte)serviceId;
        section[10] = 0xff;
        section[11] = 0xc8;
        uint crc = MpegCrc32(section[..12]);
        section[12] = (byte)(crc >> 24);
        section[13] = (byte)(crc >> 16);
        section[14] = (byte)(crc >> 8);
        section[15] = (byte)crc;
        return packet;
    }

    private static uint MpegCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc;
    }
}
