using System.Text;
using SRdeckPlugin.Aprs.Models;

namespace SRdeckPlugin.Aprs.Protocols;

public static class Ax25Decoder
{
    // CRC-16/X-25, reflected polynomial, init/xorout = ffff.
    public static ushort ComputeFcs(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xffff;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0x8408 : 0));
        }
        return (ushort)~crc;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out Ax25Frame? frame)
    {
        frame = null;
        if (data.Length is < 18 or > 330 ||
            ComputeFcs(data[..^2]) != (ushort)(data[^2] | data[^1] << 8)) return false;
        var addresses = new List<string>(10);
        int offset = 0;
        bool last = false;
        Span<char> call = stackalloc char[6];
        while (!last && addresses.Count < 10)
        {
            if (offset + 7 > data.Length - 4) return false;
            for (int i = 0; i < 6; i++)
            {
                byte b = data[offset + i];
                char c = (char)(b >> 1);
                if ((b & 1) != 0 || !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ')) return false;
                call[i] = c;
            }
            string name = new string(call).TrimEnd();
            if (name.Length == 0 || name.Contains(' ')) return false;
            byte flags = data[offset + 6];
            int ssid = (flags >> 1) & 15;
            if (ssid != 0) name += $"-{ssid}";
            if (addresses.Count >= 2 && (flags & 0x80) != 0) name += "*";
            addresses.Add(name);
            last = (flags & 1) != 0;
            offset += 7;
        }
        if (!last || addresses.Count < 2 || offset + 2 > data.Length - 2 ||
            data[offset] != 0x03 || data[offset + 1] != 0xf0 || data.Length - offset - 4 > 256) return false;
        frame = new(addresses[1], addresses[0], addresses.Skip(2).ToArray(),
            Encoding.Latin1.GetString(data[(offset + 2)..^2]));
        return true;
    }
}
