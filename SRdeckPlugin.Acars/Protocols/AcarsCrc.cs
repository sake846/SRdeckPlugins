namespace SRdeckPlugin.Acars.Protocols;

/// <summary>ARINC 618 block-check sequence (CRC-16/KERMIT, reflected).</summary>
public static class AcarsCrc
{
    private static readonly ushort[] Table = InitTable();

    private static ushort[] InitTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ value) & 0xFF]);
        }
        return crc;
    }

    public static bool IsValid(ReadOnlySpan<byte> bytesWithBcs)
    {
        if (bytesWithBcs.Length < 3) return false;
        ReadOnlySpan<byte> data = bytesWithBcs[..^2];
        ushort expected = Compute(data);
        ushort littleEndian = (ushort)(bytesWithBcs[^2] | bytesWithBcs[^1] << 8);
        ushort bigEndian = (ushort)(bytesWithBcs[^2] << 8 | bytesWithBcs[^1]);
        return expected == littleEndian || expected == bigEndian || Compute(bytesWithBcs) == 0;
    }
}
