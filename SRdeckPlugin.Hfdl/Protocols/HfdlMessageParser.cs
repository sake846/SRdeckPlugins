using System.Text;
using SRdeckPlugin.Hfdl.Models;

namespace SRdeckPlugin.Hfdl.Protocols;

/// <summary>Extracts the stable SPDU/LPDU envelope without exposing it to the host.</summary>
public static class HfdlMessageParser
{
    public static bool TryParse(HfdlFrame frame, out HfdlMessage? message) => TryParse(frame.Bytes, out message);

    public static bool TryParse(ReadOnlySpan<byte> frame, out HfdlMessage? message)
    {
        message = null;
        if (frame.Length < 5) return false;
        bool crcValid = HfdlCrc.IsValid(frame);
        ReadOnlySpan<byte> body = frame[..^2];
        byte type = (byte)(body[0] & 0x3f);
        int? source = body.Length >= 4 ? ReadAddress(body[1..4]) : null;
        int? destination = body.Length >= 7 ? ReadAddress(body[4..7]) : null;
        string flightId = FindFlightId(body.Length > 7 ? body[7..] : body[1..]);
        byte[] payload = body.Length > 7 ? body[7..].ToArray() : body[1..].ToArray();
        message = new(type, Kind(type), source, destination, flightId, payload, crcValid);
        return true;
    }

    private static int ReadAddress(ReadOnlySpan<byte> bytes) => bytes[0] << 16 | bytes[1] << 8 | bytes[2];

    private static string Kind(byte type) => type switch
    {
        0x00 => "System table",
        0x01 => "Log-on",
        0x02 => "Log-off",
        0x03 => "Position report",
        0x04 => "Performance data",
        0x05 => "Frequency data",
        0x06 => "User data",
        0x07 => "Acknowledgement",
        _ => $"SPDU 0x{type:X2}"
    };

    private static string FindFlightId(ReadOnlySpan<byte> bytes)
    {
        string best = string.Empty;
        var current = new StringBuilder();
        foreach (byte value in bytes)
        {
            char c = (char)(value & 0x7f);
            if (char.IsAsciiLetterOrDigit(c) || c == '-') current.Append(c);
            else { if (current.Length >= 4 && current.Length <= 8 && current.Length > best.Length) best = current.ToString(); current.Clear(); }
        }
        if (current.Length >= 4 && current.Length <= 8 && current.Length > best.Length) best = current.ToString();
        return best;
    }
}
