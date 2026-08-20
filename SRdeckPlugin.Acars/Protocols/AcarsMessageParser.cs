using System.Text;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

public static class AcarsMessageParser
{
    private const byte Syn = 0x16;
    private const byte Soh = 0x01;

    public static bool TryParse(AcarsFrame frame, out AcarsMessage? message) =>
        TryParse(frame.Bytes, out message);

    public static bool TryParse(ReadOnlySpan<byte> frame, out AcarsMessage? message)
    {
        message = null;
        int start = FindHeader(frame);
        if (start < 0 || frame.Length - start < 18) return false;

        int end = -1;
        for (int index = start + 15; index < frame.Length; index++)
        {
            byte value = (byte)(frame[index] & 0x7f);
            if (value is 0x03 or 0x17) { end = index; break; }
        }
        if (end < 0) return false;

        bool parityValid = true;
        for (int index = start; index <= end; index++)
            parityValid &= (System.Numerics.BitOperations.PopCount(frame[index]) & 1) == 1;

        string mode = Character(frame[start + 3]);
        string aircraft = Text(frame.Slice(start + 4, 7)).Trim();
        string acknowledgement = Character(frame[start + 11]);
        string label = Text(frame.Slice(start + 12, 2)).Trim();
        string blockId = Character(frame[start + 14]);
        int textStart = start + 15;
        if (textStart < end && (frame[textStart] & 0x7f) == 0x02) textStart++;
        bool isContinuationBlock = (frame[end] & 0x7f) == 0x17;
        string text = Text(frame.Slice(textStart, end - textStart));
        text = isContinuationBlock
            ? text.TrimEnd('\0', '\r', '\n')
            : text.TrimEnd('\0', '\r', '\n', ' ');

        bool crcValid = false;
        if (end + 2 < frame.Length)
        {
            // The BCS covers the mode character following SOH through ETX/ETB;
            // SOH itself is not included. Some receivers clear parity before
            // exposing bytes, so accept either representation.
            crcValid = AcarsCrc.IsValid(frame.Slice(start + 3, end - start));
            if (!crcValid)
            {
                ReadOnlySpan<byte> slice = frame.Slice(start + 3, end - start);
                Span<byte> normalized = slice.Length <= 512 ? stackalloc byte[slice.Length] : new byte[slice.Length];
                slice.CopyTo(normalized);
                for (int index = 0; index < normalized.Length - 2; index++) normalized[index] &= 0x7f;
                crcValid = AcarsCrc.IsValid(normalized);
            }
        }

        message = new(mode, aircraft, acknowledgement, label, blockId, text, crcValid, parityValid,
            isContinuationBlock);
        return aircraft.Length > 0 && label.Length > 0;
    }

    private static int FindHeader(ReadOnlySpan<byte> frame)
    {
        for (int index = 0; index + 2 < frame.Length; index++)
            if ((frame[index] & 0x7f) == Syn && (frame[index + 1] & 0x7f) == Syn &&
                (frame[index + 2] & 0x7f) == Soh) return index;
        return -1;
    }

    private static string Character(byte value)
    {
        char character = (char)(value & 0x7f);
        return char.IsControl(character) ? string.Empty : character.ToString();
    }

    private static string Text(ReadOnlySpan<byte> values)
    {
        var builder = new StringBuilder(values.Length);
        foreach (byte value in values)
        {
            char character = (char)(value & 0x7f);
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString();
    }
}
