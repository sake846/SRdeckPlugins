using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SRdeckPlugin.Ft8.Protocols;

/// <summary>
/// FT8 CRC-14, LDPC(174,91) decoder, and 77-bit message unpacker implemented
/// from the published FT8 protocol definition.  See docs/ft8-protocol.md.
/// </summary>
public sealed class Ft8Codec
{
    public const int CodewordBits = 174;
    private const int PayloadAndCrcBits = 91;
    private const uint Max22 = 4_194_304;
    private const uint Tokens = 2_063_592;
    private const ushort MaxGrid4 = 32_400;
    private readonly Dictionary<uint, string> callsigns = [];
    // Each variable belongs to three parity checks.  The normalized min-sum
    // schedule below uses one directed value per edge in each direction.
    private readonly float[] variableToCheck = new float[CodewordBits * 3];
    private readonly float[] checkToVariable = new float[CodewordBits * 3];

    public sealed record DecodedMessage(
        string Text,
        string Type,
        string ToCall,
        string FromCall,
        string Extra,
        byte[] Payload);

    public bool TryDecode(ReadOnlySpan<float> likelihood, int maximumIterations,
        out DecodedMessage? message, out int parityErrors, out bool crcValid)
    {
        if (likelihood.Length != CodewordBits)
            throw new ArgumentException("An FT8 codeword contains 174 likelihoods.", nameof(likelihood));

        Span<byte> bits = stackalloc byte[CodewordBits];
        parityErrors = DecodeLdpc(likelihood, maximumIterations, bits);
        crcValid = false;
        message = null;
        if (parityErrors != 0) return false;

        Span<byte> packed = stackalloc byte[12];
        PackBits(bits[..PayloadAndCrcBits], packed);
        ushort extracted = (ushort)(((packed[9] & 7) << 11) | (packed[10] << 3) | (packed[11] >> 5));
        packed[9] &= 0xF8;
        packed[10] = 0;
        ushort calculated = ComputeCrc(packed, 82);
        if (extracted != calculated) return false;
        crcValid = true;

        // All-zero information bits encode an empty free-text field.  It is a
        // valid codeword but not an over-the-air message and must not occupy a
        // decode result or seed a cancellation pass.
        if (packed[..10].IndexOfAnyExcept((byte)0) < 0) return false;

        byte[] payload = packed[..10].ToArray();
        DecodedMessage unpacked = Unpack(payload);
        if (string.IsNullOrWhiteSpace(unpacked.Text))
        {
            message = null;
            return false;
        }
        message = unpacked;
        return true;
    }

    public DecodedMessage Unpack(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10) throw new ArgumentException("FT8 payload must contain 10 bytes.", nameof(payload));
        byte[] copy = payload[..10].ToArray();
        int i3 = (payload[9] >> 3) & 7;
        int n3 = ((payload[8] << 2) & 4) | ((payload[9] >> 6) & 3);

        if (i3 is 1 or 2) return UnpackStandard(copy, i3);
        if (i3 == 4) return UnpackNonstandard(copy);
        if (i3 == 0 && n3 == 0)
        {
            string text = DecodeFreeText(copy);
            return new(text, "自由文", "", "", "", copy);
        }
        if (i3 == 0 && n3 == 5)
        {
            string text = DecodeTelemetry(copy);
            return new(text, "テレメトリ", "", "", "", copy);
        }

        string raw = Convert.ToHexString(copy.AsSpan(0, 9)) +
                     ((copy[9] >> 3) & 0x0F).ToString("X1", CultureInfo.InvariantCulture);
        string type = (i3, n3) switch
        {
            (0, 1) => "DXペディション",
            (0, 2) => "EU VHFコンテスト",
            (0, 3 or 4) => "ARRL Field Day",
            (3, _) => "ARRL RTTY",
            (5, _) => "WWROFコンテスト",
            _ => $"予約種別 i3={i3}, n3={n3}"
        };
        return new($"{type} / RAW {raw}", type, "", "", "", copy);
    }

    private DecodedMessage UnpackStandard(byte[] payload, int i3)
    {
        uint n29a = ((uint)payload[0] << 21) | ((uint)payload[1] << 13) |
                    ((uint)payload[2] << 5) | ((uint)payload[3] >> 3);
        uint n29b = ((uint)(payload[3] & 7) << 26) | ((uint)payload[4] << 18) |
                    ((uint)payload[5] << 10) | ((uint)payload[6] << 2) | ((uint)payload[7] >> 6);
        int ir = (payload[7] >> 5) & 1;
        int grid = ((payload[7] & 0x1F) << 10) | (payload[8] << 2) | (payload[9] >> 6);

        string to = Unpack28(n29a >> 1, (int)(n29a & 1), i3);
        string from = Unpack28(n29b >> 1, (int)(n29b & 1), i3);
        string extra = UnpackGrid(grid, ir);
        string text = string.Join(' ', new[] { to, from, extra }.Where(value => value.Length > 0));
        return new(text, "標準", to, from, extra, payload);
    }

    private DecodedMessage UnpackNonstandard(byte[] payload)
    {
        uint n12 = (uint)((payload[0] << 4) | (payload[1] >> 4));
        ulong n58 = ((ulong)(payload[1] & 0x0F) << 54) | ((ulong)payload[2] << 46) |
                    ((ulong)payload[3] << 38) | ((ulong)payload[4] << 30) |
                    ((ulong)payload[5] << 22) | ((ulong)payload[6] << 14) |
                    ((ulong)payload[7] << 6) | ((ulong)payload[8] >> 2);
        int flip = (payload[8] >> 1) & 1;
        int report = ((payload[8] & 1) << 1) | (payload[9] >> 7);
        bool cq = ((payload[9] >> 6) & 1) != 0;

        string decoded = Unpack58(n58);
        SaveCallsign(decoded);
        string hashed = LookupHash(n12, 12);
        string first = flip != 0 ? decoded : hashed;
        string second = flip != 0 ? hashed : decoded;
        string to = cq ? "CQ" : first;
        string from = second;
        string extra = cq ? "" : report switch { 1 => "RRR", 2 => "RR73", 3 => "73", _ => "" };
        string text = string.Join(' ', new[] { to, from, extra }.Where(value => value.Length > 0));
        return new(text, "非標準コール", to, from, extra, payload);
    }

    private string Unpack28(uint value, int suffix, int i3)
    {
        if (value < Tokens)
        {
            if (value == 0) return "DE";
            if (value == 1) return "QRZ";
            if (value == 2) return "CQ";
            if (value <= 1002) return $"CQ {value - 3:000}";
            if (value <= 532443)
            {
                uint n = value - 1003;
                Span<char> chars = stackalloc char[4];
                for (int index = 3; index >= 0; index--)
                {
                    chars[index] = LetterSpace((int)(n % 27));
                    n /= 27;
                }
                return $"CQ {new string(chars).TrimStart()}";
            }
            return "<...>";
        }

        value -= Tokens;
        if (value < Max22) return LookupHash(value, 22);
        uint ncall = value - Max22;
        Span<char> call = stackalloc char[6];
        call[5] = LetterSpace((int)(ncall % 27)); ncall /= 27;
        call[4] = LetterSpace((int)(ncall % 27)); ncall /= 27;
        call[3] = LetterSpace((int)(ncall % 27)); ncall /= 27;
        call[2] = (char)('0' + ncall % 10); ncall /= 10;
        call[1] = AlphaNumeric((int)(ncall % 36)); ncall /= 36;
        call[0] = AlphaNumericSpace((int)(ncall % 37));
        string result = new string(call).Trim();
        if (result.StartsWith("3D0", StringComparison.Ordinal) && result.Length > 3)
            result = "3DA0" + result[3..];
        else if (result.Length > 1 && result[0] == 'Q' && char.IsLetter(result[1]))
            result = "3X" + result[1..];
        if (suffix != 0) result += i3 switch { 1 => "/R", 2 => "/P", _ => "" };
        SaveCallsign(result);
        return result;
    }

    private static string UnpackGrid(int value, int ir)
    {
        if (value <= MaxGrid4)
        {
            int n = value;
            char d = (char)('0' + n % 10); n /= 10;
            char c = (char)('0' + n % 10); n /= 10;
            char b = (char)('A' + n % 18); n /= 18;
            char a = (char)('A' + n % 18);
            return (ir != 0 ? "R " : "") + new string([a, b, c, d]);
        }
        int report = value - MaxGrid4;
        return report switch
        {
            1 => "",
            2 => "RRR",
            3 => "RR73",
            4 => "73",
            _ => (ir != 0 ? "R" : "") + (report - 35).ToString("+00;-00;+00", CultureInfo.InvariantCulture)
        };
    }

    private static string DecodeFreeText(ReadOnlySpan<byte> payload)
    {
        byte[] number = RightAlign71(payload);
        Span<char> text = stackalloc char[13];
        const string alphabet = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
        for (int index = 12; index >= 0; index--)
        {
            int remainder = 0;
            for (int i = 0; i < number.Length; i++)
            {
                int value = (remainder << 8) | number[i];
                number[i] = (byte)(value / 42);
                remainder = value % 42;
            }
            text[index] = alphabet[remainder];
        }
        return new string(text).Trim();
    }

    private static string DecodeTelemetry(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(RightAlign71(payload));

    private static byte[] RightAlign71(ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[9];
        int carry = 0;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((carry << 7) | (payload[i] >> 1));
            carry = payload[i] & 1;
        }
        return result;
    }

    private static string Unpack58(ulong value)
    {
        Span<char> call = stackalloc char[11];
        for (int index = 10; index >= 0; index--)
        {
            int symbol = (int)(value % 38);
            value /= 38;
            call[index] = symbol switch
            {
                0 => ' ',
                <= 10 => (char)('0' + symbol - 1),
                <= 36 => (char)('A' + symbol - 11),
                _ => '/'
            };
        }
        return new string(call).Trim();
    }

    private string LookupHash(uint hash, int width)
    {
        foreach ((uint full, string call) in callsigns)
            if ((width == 22 ? full : full >> (22 - width)) == hash) return $"<{call}>";
        return "<...>";
    }

    private void SaveCallsign(string callsign)
    {
        callsign = callsign.Trim('<', '>');
        if (callsign.Length < 3 || callsign.Length > 11) return;
        ulong n58 = 0;
        for (int i = 0; i < 11; i++)
        {
            char c = i < callsign.Length ? callsign[i] : ' ';
            int value = c switch
            {
                ' ' => 0,
                >= '0' and <= '9' => c - '0' + 1,
                >= 'A' and <= 'Z' => c - 'A' + 11,
                '/' => 37,
                _ => -1
            };
            if (value < 0) return;
            n58 = unchecked(n58 * 38 + (uint)value);
        }
        uint hash = (uint)(unchecked(47_055_833_459UL * n58) >> 42) & 0x3FFFFF;
        callsigns[hash] = callsign;
        if (callsigns.Count > 1024) callsigns.Remove(callsigns.Keys.First());
    }

    public static ushort ComputeCrc(ReadOnlySpan<byte> data, int bitCount)
    {
        const ushort polynomial = 0x2757;
        const ushort topBit = 1 << 13;
        ushort remainder = 0;
        int byteIndex = 0;
        for (int bit = 0; bit < bitCount; bit++)
        {
            if ((bit & 7) == 0) remainder ^= (ushort)(data[byteIndex++] << 6);
            remainder = (ushort)((remainder & topBit) != 0
                ? (remainder << 1) ^ polynomial
                : remainder << 1);
        }
        return (ushort)(remainder & 0x3FFF);
    }

    internal static byte[] EncodeCodeword(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new ArgumentException("FT8 payload must contain 10 bytes.", nameof(payload));
        Span<byte> packed = stackalloc byte[12];
        payload[..10].CopyTo(packed);
        packed[9] &= 0xF8;
        ushort crc = ComputeCrc(packed, 82);
        packed[9] |= (byte)(crc >> 11);
        packed[10] = (byte)(crc >> 3);
        packed[11] = (byte)(crc << 5);

        var information = new byte[PayloadAndCrcBits];
        for (int bit = 0; bit < information.Length; bit++)
            information[bit] = (byte)((packed[bit >> 3] >> (7 - (bit & 7))) & 1);
        var codeword = new byte[CodewordBits];
        information.CopyTo(codeword, 0);
        for (int parity = 0; parity < ParityChecks.Length; parity++)
        {
            byte value = 0;
            for (int bit = 0; bit < information.Length; bit++)
                value ^= (byte)(information[bit] * SystematicGenerator[bit, PayloadAndCrcBits + parity]);
            codeword[PayloadAndCrcBits + parity] = value;
        }
        return codeword;
    }

    private static void PackBits(ReadOnlySpan<byte> bits, Span<byte> packed)
    {
        packed.Clear();
        for (int bit = 0; bit < bits.Length; bit++)
            if (bits[bit] != 0) packed[bit >> 3] |= (byte)(0x80 >> (bit & 7));
    }

    private int DecodeLdpc(ReadOnlySpan<float> channel, int maximumIterations, Span<byte> plain)
    {
        for (int edge = 0; edge < variableToCheck.Length; edge++)
            variableToCheck[edge] = channel[edge / 3];

        int minimumErrors = ParityChecks.Length;
        for (int iteration = 0; iteration < maximumIterations; iteration++)
        {
            // Check-node update for the sum-product decoder.  Values use the
            // log(P(bit=1)/P(bit=0)) convention adopted by this receiver.
            foreach (int[] checkEdges in EdgesByCheck)
            {
                foreach (int destination in checkEdges)
                {
                    float product = 1;
                    foreach (int source in checkEdges)
                    {
                        if (source == destination) continue;
                        product *= MathF.Tanh(-0.5f * variableToCheck[source]);
                    }
                    product = Math.Clamp(product, -0.999999f, 0.999999f);
                    checkToVariable[destination] = -MathF.Log((1 + product) / (1 - product));
                }
            }

            for (int bit = 0; bit < CodewordBits; bit++)
            {
                int edge = bit * 3;
                float posterior = channel[bit] + checkToVariable[edge] +
                                  checkToVariable[edge + 1] + checkToVariable[edge + 2];
                plain[bit] = posterior >= 0 ? (byte)1 : (byte)0;
                variableToCheck[edge] = posterior - checkToVariable[edge];
                variableToCheck[edge + 1] = posterior - checkToVariable[edge + 1];
                variableToCheck[edge + 2] = posterior - checkToVariable[edge + 2];
            }

            int errors = CheckParity(plain);
            minimumErrors = Math.Min(minimumErrors, errors);
            if (errors == 0) return 0;
        }
        return minimumErrors;
    }

    private static int CheckParity(ReadOnlySpan<byte> bits)
    {
        int errors = 0;
        foreach (byte[] row in ParityChecks)
        {
            int parity = 0;
            foreach (byte bit in row) parity ^= bits[bit];
            errors += parity;
        }
        return errors;
    }

    private static char LetterSpace(int value) => value == 0 ? ' ' : (char)('A' + value - 1);
    private static char AlphaNumeric(int value) => value < 10 ? (char)('0' + value) : (char)('A' + value - 10);
    private static char AlphaNumericSpace(int value) => value == 0 ? ' ' : AlphaNumeric(value - 1);

    // Sparse parity-check matrix defined by parity.dat in the public-domain
    // FT4/FT8 protocol reference package.  Rows are reconstructed from that
    // document's column triples and use zero-based indices internally.
    private static readonly byte[][] ParityChecks =
    [
        R(4,31,59,91,92,96,153), R(5,32,60,93,115,146), R(6,24,61,94,122,151),
        R(7,33,62,95,96,143), R(8,25,63,83,93,96,148), R(6,32,64,97,126,138),
        R(5,34,65,78,98,107,154), R(9,35,66,99,139,146), R(10,36,67,100,107,126),
        R(11,37,67,87,101,139,158), R(12,38,68,102,105,155), R(13,39,69,103,149,162),
        R(8,40,70,82,104,114,145), R(14,41,71,88,102,123,156), R(15,42,59,106,123,159),
        R(1,33,72,106,107,157), R(16,43,73,108,141,160), R(17,37,74,81,109,131,154),
        R(11,44,75,110,121,166), R(45,55,64,111,130,161,173), R(8,46,71,112,119,166),
        R(18,36,76,89,113,114,143), R(19,38,77,104,116,163), R(20,47,70,92,138,165),
        R(2,48,74,113,128,160), R(21,45,78,83,117,121,151), R(22,47,58,118,127,164),
        R(16,39,62,112,134,158), R(23,43,79,120,131,145), R(19,35,59,73,110,125,161),
        R(20,36,63,94,136,161), R(14,31,79,98,132,164), R(3,44,80,124,127,169),
        R(19,46,81,117,135,167), R(7,49,58,90,100,105,168), R(12,50,61,118,119,144),
        R(13,51,64,114,118,157), R(24,52,76,129,148,149), R(25,53,69,90,101,130,156),
        R(20,46,65,80,120,140,170), R(21,54,77,100,140,171), R(35,82,133,142,171,174),
        R(14,30,83,113,125,170), R(4,29,68,120,134,173), R(1,4,52,57,86,136,152),
        R(26,51,56,91,122,137,168), R(52,84,110,115,145,168), R(7,50,81,99,132,173),
        R(23,55,67,95,172,174), R(26,41,77,109,141,148), R(2,27,41,61,62,115,133),
        R(27,40,56,124,125,126), R(18,49,55,124,141,167), R(6,33,85,108,116,156),
        R(28,48,70,85,105,129,158), R(9,54,63,131,147,155), R(22,53,68,109,121,174),
        R(3,13,48,78,95,123), R(31,69,133,150,155,169), R(12,43,66,89,97,135,159),
        R(5,39,75,102,136,167), R(2,54,86,101,135,164), R(15,56,87,108,119,171),
        R(10,44,82,91,111,144,149), R(23,34,71,94,127,153), R(11,49,88,92,142,157),
        R(29,34,87,97,147,162), R(30,50,60,86,137,142,162), R(10,53,66,84,112,128,165),
        R(22,57,85,93,140,159), R(28,32,72,103,132,166), R(28,29,84,88,117,143,150),
        R(1,26,45,80,128,147), R(17,27,89,103,116,153), R(51,57,98,163,165,172),
        R(21,37,73,138,152,169), R(16,47,76,130,137,154), R(3,24,30,72,104,139),
        R(9,40,90,106,134,151), R(15,58,60,74,111,150,163), R(18,42,79,144,146,152),
        R(25,38,65,99,122,160), R(17,42,75,129,170,172)
    ];

    private static readonly int[][] EdgesByCheck = BuildEdgesByCheck();
    private static readonly byte[,] SystematicGenerator = BuildSystematicGenerator();

    private static byte[] R(params int[] oneBased) =>
        oneBased.Select(value => checked((byte)(value - 1))).ToArray();

    private static int[][] BuildEdgesByCheck()
    {
        var result = new List<int>[ParityChecks.Length];
        for (int check = 0; check < result.Length; check++) result[check] = [];
        int[] counts = new int[CodewordBits];
        for (int check = 0; check < ParityChecks.Length; check++)
        {
            foreach (byte bit in ParityChecks[check])
                result[check].Add(bit * 3 + counts[bit]++);
        }
        if (counts.Any(count => count != 3))
            throw new InvalidOperationException("Invalid FT8 LDPC parity-check matrix.");
        return result.Select(edges => edges.ToArray()).ToArray();
    }

    private static byte[,] BuildSystematicGenerator()
    {
        int parityBits = ParityChecks.Length;
        var inverse = new byte[parityBits, parityBits * 2];
        for (int row = 0; row < parityBits; row++)
        {
            foreach (byte bit in ParityChecks[row])
                if (bit >= PayloadAndCrcBits)
                    inverse[row, bit - PayloadAndCrcBits] = 1;
            inverse[row, parityBits + row] = 1;
        }
        for (int column = 0; column < parityBits; column++)
        {
            int pivot = column;
            while (pivot < parityBits && inverse[pivot, column] == 0) pivot++;
            if (pivot == parityBits)
                throw new InvalidOperationException("FT8 LDPC parity submatrix is singular.");
            if (pivot != column)
                for (int item = 0; item < parityBits * 2; item++)
                    (inverse[column, item], inverse[pivot, item]) =
                        (inverse[pivot, item], inverse[column, item]);
            for (int row = 0; row < parityBits; row++)
            {
                if (row == column || inverse[row, column] == 0) continue;
                for (int item = column; item < parityBits * 2; item++)
                    inverse[row, item] ^= inverse[column, item];
            }
        }

        var result = new byte[PayloadAndCrcBits, CodewordBits];
        for (int information = 0; information < PayloadAndCrcBits; information++)
        {
            result[information, information] = 1;
            for (int parity = 0; parity < parityBits; parity++)
            {
                byte value = 0;
                for (int check = 0; check < parityBits; check++)
                {
                    if (inverse[parity, parityBits + check] != 0 &&
                         ParityChecks[check].Contains((byte)information))
                        value ^= 1;
                }
                result[information, PayloadAndCrcBits + parity] = value;
            }
        }
        return result;
    }
}
