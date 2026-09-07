namespace SRdeckPlugin.OneSeg.Transport;

/// <summary>
/// A bitmap glyph carried by an ARIB DRCS data unit.
/// </summary>
internal sealed record AribDrcsGlyph(
    int Set,
    int Code,
    byte FontId,
    byte Mode,
    int Depth,
    int Width,
    int Height,
    int DepthBits,
    byte[] Pixels);

/// <summary>
/// Keeps DRCS definitions received on one caption stream.
/// </summary>
internal sealed class AribDrcsStore
{
    private readonly Dictionary<(int Set, int Code, byte FontId), AribDrcsGlyph> glyphs = [];

    public void Clear() => glyphs.Clear();

    public bool TryGet(int set, int code, out AribDrcsGlyph glyph)
    {
        if (glyphs.TryGetValue((set, code, 0), out glyph!)) return true;

        // The caption decoder does not currently carry an explicit font selector.
        // A stream's first font is normally the one used, so accept another font as
        // a useful fallback instead of losing the definition completely.
        foreach (var pair in glyphs)
        {
            if (pair.Key.Set == set && pair.Key.Code == code)
            {
                glyph = pair.Value;
                return true;
            }
        }

        glyph = null!;
        return false;
    }

    /// <summary>
    /// Parses one 1-byte (0x30) or 2-byte (0x31) DRCS pattern data unit.
    /// </summary>
    public IReadOnlyList<AribDrcsGlyph> Register(ReadOnlySpan<byte> source, int bytesPerCode)
    {
        var registered = new List<AribDrcsGlyph>();
        if (bytesPerCode is not (1 or 2) || source.Length == 0) return registered;

        int offset = 1;
        int numberOfCodes = source[0];
        for (int codeIndex = 0; codeIndex < numberOfCodes; codeIndex++)
        {
            if (offset + 3 > source.Length) break;
            ushort characterCode = (ushort)((source[offset] << 8) | source[offset + 1]);
            int numberOfFonts = source[offset + 2];
            offset += 3;

            for (int fontIndex = 0; fontIndex < numberOfFonts; fontIndex++)
            {
                if (offset >= source.Length) return registered;
                byte fontAndMode = source[offset++];
                byte fontId = (byte)(fontAndMode >> 4);
                byte mode = (byte)(fontAndMode & 0x0f);

                if (mode is 0 or 1)
                {
                    if (offset + 3 > source.Length) return registered;
                    int depth = source[offset++] + 2;
                    int width = source[offset++];
                    int height = source[offset++];
                    int depthBits = GetDepthBits(depth);
                    long bitmapBits = (long)width * height * depthBits;
                    long bitmapBytes = (bitmapBits + 7) / 8;
                    if (bitmapBytes > int.MaxValue || bitmapBytes > source.Length - offset)
                        return registered;

                    byte[] pixels = source.Slice(offset, (int)bitmapBytes).ToArray();
                    offset += (int)bitmapBytes;

                    if (!TryGetKey(characterCode, bytesPerCode, out int set, out int code))
                        continue;

                    var glyph = new AribDrcsGlyph(
                        set, code, fontId, mode, depth, width, height, depthBits, pixels);
                    glyphs[(set, code, fontId)] = glyph;
                    registered.Add(glyph);
                }
                else
                {
                    // Geometric/compressed DRCS has a region (X/Y) followed by a
                    // 16-bit payload length. Preserve stream alignment even though
                    // the current text-only presenter only consumes bitmap DRCS.
                    if (offset + 4 > source.Length) return registered;
                    offset += 2;
                    int geometricLength = (source[offset] << 8) | source[offset + 1];
                    offset += 2;
                    if (geometricLength > source.Length - offset) return registered;
                    offset += geometricLength;
                }
            }
        }

        return registered;
    }

    private static int GetDepthBits(int depth)
    {
        int bits = 0;
        for (int levels = depth - 1; levels > 0; levels >>= 1) bits++;
        return Math.Max(bits, 1);
    }

    private static bool TryGetKey(ushort characterCode, int bytesPerCode, out int set, out int code)
    {
        if (bytesPerCode == 1)
        {
            set = (characterCode >> 8) & 0x0f;
            code = characterCode & 0x7f;
            return set is >= 1 and <= 15;
        }

        set = 0;
        code = characterCode is >= 0xec00 and <= 0xf8ff
            ? characterCode
            : characterCode & 0x7f7f;
        return true;
    }
}
