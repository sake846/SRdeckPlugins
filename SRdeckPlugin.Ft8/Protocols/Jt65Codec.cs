// The Reed-Solomon routines in this file are adapted from Phil Karn's 2002
// GPL-licensed codec distributed with WSJT-X. See THIRD-PARTY-NOTICES.md.
namespace SRdeckPlugin.Ft8.Protocols;

/// <summary>JT65 72-bit message and shortened RS(63,12) codec.</summary>
public sealed class Jt65Codec
{
    private const int Nn = 63;
    private const int Roots = 51;
    private const int A0 = Nn;
    private const int FirstRoot = 3;
    private const int Primitive = 1;
    private const int NBase = 37 * 36 * 10 * 27 * 27 * 27;
    private readonly int[] alphaTo = new int[Nn + 1];
    private readonly int[] indexOf = new int[Nn + 1];
    private readonly int[] generator = new int[Roots + 1];

    public sealed record DecodedMessage(string Text, string Type, string FromCall,
        string ToCall, string Extra, byte[] Payload);

    public Jt65Codec() => InitializeField();

    public bool TryDecode(ReadOnlySpan<byte> receivedChannelSymbols,
        out DecodedMessage? message, out int correctedErrors)
    {
        message = null;
        correctedErrors = -1;
        if (receivedChannelSymbols.Length < Nn) return false;
        Span<byte> sent = stackalloc byte[Nn];
        for (int index = 0; index < Nn; index++)
            sent[index] = (byte)GrayDecode(receivedChannelSymbols[index] & 63);
        Deinterleave(sent);

        var codeword = new int[Nn];
        for (int index = 0; index < 12; index++) codeword[index] = sent[62 - index];
        for (int index = 0; index < Roots; index++) codeword[12 + index] = sent[50 - index];
        correctedErrors = DecodeRs(codeword);
        if (correctedErrors < 0) return false;
        var payload = new byte[12];
        for (int index = 0; index < 12; index++) payload[index] = (byte)codeword[11 - index];
        message = Unpack(payload);
        return !string.IsNullOrWhiteSpace(message.Text) && !message.Text.Contains('.', StringComparison.Ordinal);
    }

    public DecodedMessage Unpack(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new ArgumentException("JT65 payload must contain 12 symbols.", nameof(data));
        uint call1 = ((uint)data[0] << 22) | ((uint)data[1] << 16) |
                     ((uint)data[2] << 10) | ((uint)data[3] << 4) | ((uint)data[4] >> 2);
        uint call2 = ((uint)(data[4] & 3) << 26) | ((uint)data[5] << 20) |
                     ((uint)data[6] << 14) | ((uint)data[7] << 8) |
                     ((uint)data[8] << 2) | ((uint)data[9] >> 4);
        int gridValue = ((data[9] & 15) << 12) | (data[10] << 6) | data[11];

        if (gridValue >= 32768)
        {
            string text = UnpackFreeText(call1, call2, gridValue).Trim();
            return new(text, "free-text", string.Empty, string.Empty, string.Empty,
                data[..12].ToArray());
        }

        string first = UnpackCall(call1);
        string second = UnpackCall(call2);
        string extra = UnpackGrid(gridValue);
        string textValue = string.Join(' ', new[] { first, second, extra }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        string to = first;
        string from = second;
        return new(textValue, "standard", from, to, extra, data[..12].ToArray());
    }

    internal byte[] EncodeChannelSymbols(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) throw new ArgumentException("JT65 payload must contain 12 symbols.", nameof(payload));
        var data = new int[12];
        for (int index = 0; index < 12; index++) data[index] = payload[11 - index] & 63;
        int[] parity = EncodeRs(data);
        var sent = new byte[Nn];
        for (int index = 0; index < Roots; index++) sent[50 - index] = (byte)parity[index];
        for (int index = 0; index < 12; index++) sent[51 + index] = payload[index];
        Interleave(sent);
        for (int index = 0; index < sent.Length; index++)
            sent[index] = (byte)(sent[index] ^ (sent[index] >> 1));
        return sent;
    }

    private string UnpackCall(uint packed)
    {
        if (packed == NBase + 1) return "CQ";
        if (packed == NBase + 2) return "QRZ";
        if (packed >= NBase + 3 && packed <= NBase + 1002)
            return $"CQ {packed - NBase - 3:000}";
        if (packed >= NBase) return "......";
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";
        Span<char> result = stackalloc char[6];
        uint value = packed;
        result[5] = alphabet[(int)(value % 27) + 10]; value /= 27;
        result[4] = alphabet[(int)(value % 27) + 10]; value /= 27;
        result[3] = alphabet[(int)(value % 27) + 10]; value /= 27;
        result[2] = alphabet[(int)(value % 10)]; value /= 10;
        result[1] = alphabet[(int)(value % 36)]; value /= 36;
        result[0] = alphabet[(int)value];
        return new string(result).Trim();
    }

    private static string UnpackGrid(int value)
    {
        const int baseValue = 180 * 180;
        if (value >= baseValue)
        {
            int report = value - baseValue - 1;
            if (report is >= 1 and <= 30) return $"-{report:00}";
            if (report is >= 31 and <= 60) return $"R-{report - 30:00}";
            return report switch { 61 => "RO", 62 => "RRR", 63 => "73", _ => string.Empty };
        }
        int latitudeIndex = value % 180;
        int longitudeIndex = value / 180;
        int longitude = longitudeIndex * 2;
        int latitude = latitudeIndex;
        char fieldLongitude = (char)('A' + Math.Clamp(longitude / 20, 0, 17));
        char fieldLatitude = (char)('A' + Math.Clamp(latitude / 10, 0, 17));
        char squareLongitude = (char)('0' + longitude % 20 / 2);
        char squareLatitude = (char)('0' + latitude % 10);
        string grid = $"{fieldLongitude}{fieldLatitude}{squareLongitude}{squareLatitude}";
        if (grid.StartsWith("KA", StringComparison.Ordinal))
            return $"{int.Parse(grid[2..]) - 50:+00;-00;00}";
        if (grid.StartsWith("LA", StringComparison.Ordinal))
            return $"R{int.Parse(grid[2..]) - 50:+00;-00;00}";
        return grid;
    }

    private static string UnpackFreeText(uint call1, uint call2, int grid)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-./?";
        ulong third = (uint)(grid & 32767);
        if ((call1 & 1) != 0) third += 32768;
        call1 >>= 1;
        if ((call2 & 1) != 0) third += 65536;
        call2 >>= 1;
        Span<char> output = stackalloc char[13];
        for (int index = 4; index >= 0; index--) { output[index] = alphabet[(int)(call1 % 42)]; call1 /= 42; }
        for (int index = 9; index >= 5; index--) { output[index] = alphabet[(int)(call2 % 42)]; call2 /= 42; }
        for (int index = 12; index >= 10; index--) { output[index] = alphabet[(int)(third % 42)]; third /= 42; }
        return new string(output);
    }

    private void InitializeField()
    {
        indexOf[0] = A0;
        alphaTo[A0] = 0;
        int state = 1;
        for (int index = 0; index < Nn; index++)
        {
            indexOf[state] = index;
            alphaTo[index] = state;
            state <<= 1;
            if ((state & 64) != 0) state ^= 0x43;
            state &= Nn;
        }
        generator[0] = 1;
        for (int index = 0, root = FirstRoot; index < Roots; index++, root++)
        {
            generator[index + 1] = 1;
            for (int position = index; position > 0; position--)
                generator[position] = generator[position] != 0
                    ? generator[position - 1] ^ alphaTo[Mod(indexOf[generator[position]] + root)]
                    : generator[position - 1];
            generator[0] = alphaTo[Mod(indexOf[generator[0]] + root)];
        }
        for (int index = 0; index <= Roots; index++) generator[index] = indexOf[generator[index]];
    }

    private int[] EncodeRs(ReadOnlySpan<int> data)
    {
        var parity = new int[Roots];
        for (int index = 0; index < 12; index++)
        {
            int feedback = indexOf[data[index] ^ parity[0]];
            if (feedback != A0)
                for (int root = 1; root < Roots; root++)
                    parity[root] ^= alphaTo[Mod(feedback + generator[Roots - root])];
            Array.Copy(parity, 1, parity, 0, Roots - 1);
            parity[Roots - 1] = feedback != A0
                ? alphaTo[Mod(feedback + generator[0])] : 0;
        }
        return parity;
    }

    private int DecodeRs(int[] data)
    {
        var syndrome = new int[Roots];
        for (int index = 0; index < Roots; index++) syndrome[index] = data[0];
        for (int position = 1; position < Nn; position++)
            for (int index = 0; index < Roots; index++)
                syndrome[index] = syndrome[index] == 0 ? data[position] :
                    data[position] ^ alphaTo[Mod(indexOf[syndrome[index]] + FirstRoot + index)];
        int syndromeError = 0;
        for (int index = 0; index < Roots; index++)
        {
            syndromeError |= syndrome[index];
            syndrome[index] = indexOf[syndrome[index]];
        }
        if (syndromeError == 0) return 0;

        var lambda = new int[Roots + 1];
        var b = new int[Roots + 1];
        var t = new int[Roots + 1];
        lambda[0] = 1;
        for (int index = 0; index <= Roots; index++) b[index] = indexOf[lambda[index]];
        int r = 0, el = 0;
        while (++r <= Roots)
        {
            int discrepancy = 0;
            for (int index = 0; index < r; index++)
                if (lambda[index] != 0 && syndrome[r - index - 1] != A0)
                    discrepancy ^= alphaTo[Mod(indexOf[lambda[index]] + syndrome[r - index - 1])];
            discrepancy = indexOf[discrepancy];
            if (discrepancy == A0)
            {
                Array.Copy(b, 0, b, 1, Roots);
                b[0] = A0;
                continue;
            }
            t[0] = lambda[0];
            for (int index = 0; index < Roots; index++)
                t[index + 1] = b[index] != A0
                    ? lambda[index + 1] ^ alphaTo[Mod(discrepancy + b[index])]
                    : lambda[index + 1];
            if (2 * el <= r - 1)
            {
                el = r - el;
                for (int index = 0; index <= Roots; index++)
                    b[index] = lambda[index] == 0 ? A0 : Mod(indexOf[lambda[index]] - discrepancy + Nn);
            }
            else
            {
                Array.Copy(b, 0, b, 1, Roots);
                b[0] = A0;
            }
            Array.Copy(t, lambda, Roots + 1);
        }

        int degree = 0;
        for (int index = 0; index <= Roots; index++)
        {
            lambda[index] = indexOf[lambda[index]];
            if (lambda[index] != A0) degree = index;
        }
        var register = new int[Roots + 1];
        Array.Copy(lambda, 1, register, 1, Roots);
        var roots = new int[Roots];
        var locations = new int[Roots];
        int count = 0;
        for (int index = 1, location = 0; index <= Nn; index++, location = Mod(location + 1))
        {
            int value = 1;
            for (int term = degree; term > 0; term--)
                if (register[term] != A0)
                {
                    register[term] = Mod(register[term] + term);
                    value ^= alphaTo[register[term]];
                }
            if (value != 0) continue;
            roots[count] = index;
            locations[count] = location;
            if (++count == degree) break;
        }
        if (degree != count) return -1;

        int omegaDegree = degree - 1;
        var omega = new int[Roots + 1];
        for (int index = 0; index <= omegaDegree; index++)
        {
            int value = 0;
            for (int term = index; term >= 0; term--)
                if (syndrome[index - term] != A0 && lambda[term] != A0)
                    value ^= alphaTo[Mod(syndrome[index - term] + lambda[term])];
            omega[index] = indexOf[value];
        }
        for (int error = count - 1; error >= 0; error--)
        {
            int numerator = 0;
            for (int index = omegaDegree; index >= 0; index--)
                if (omega[index] != A0) numerator ^= alphaTo[Mod(omega[index] + index * roots[error])];
            int numerator2 = alphaTo[Mod(roots[error] * (FirstRoot - 1) + Nn)];
            int denominator = 0;
            for (int index = Math.Min(degree, Roots - 1) & ~1; index >= 0; index -= 2)
                if (lambda[index + 1] != A0)
                    denominator ^= alphaTo[Mod(lambda[index + 1] + index * roots[error])];
            if (denominator == 0) return -1;
            if (numerator != 0)
                data[locations[error]] ^= alphaTo[Mod(indexOf[numerator] + indexOf[numerator2] +
                    Nn - indexOf[denominator])];
        }
        return count;
    }

    private static void Interleave(Span<byte> values)
    {
        Span<byte> copy = stackalloc byte[Nn];
        values.CopyTo(copy);
        for (int i = 0; i < 7; i++)
        for (int j = 0; j < 9; j++) values[j + 9 * i] = copy[i + 7 * j];
    }

    private static void Deinterleave(Span<byte> values)
    {
        Span<byte> copy = stackalloc byte[Nn];
        values.CopyTo(copy);
        for (int i = 0; i < 7; i++)
        for (int j = 0; j < 9; j++) values[i + 7 * j] = copy[j + 9 * i];
    }

    private static int GrayDecode(int value)
    {
        int decoded = value;
        for (int shift = 1; shift < 6; shift <<= 1) decoded ^= decoded >> shift;
        return decoded & 63;
    }

    private static int Mod(int value)
    {
        while (value >= Nn) value -= Nn;
        while (value < 0) value += Nn;
        return value;
    }
}
