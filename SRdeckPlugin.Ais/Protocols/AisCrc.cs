namespace SRdeckPlugin.Ais.Protocols;

public static class AisCrc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return (ushort)~crc;
    }

    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return false;
        ushort expected = Compute(frame[..^2]);
        return frame[^2] == (byte)expected && frame[^1] == (byte)(expected >> 8);
    }
}
