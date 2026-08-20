namespace SRdeckPlugin.Hfdl.Protocols;

public static class HfdlCrc
{
    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return (ushort)~crc;
    }

    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return false;
        ushort expected = Compute(frame[..^2]);
        ushort little = (ushort)(frame[^2] | frame[^1] << 8);
        ushort big = (ushort)(frame[^2] << 8 | frame[^1]);
        return expected == little || expected == big;
    }
}
