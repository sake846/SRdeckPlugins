namespace SRdeckPlugin.Ais.Protocols;

internal sealed class AisHdlcDecoder
{
    private const int MaximumRawBits = 2048;
    private readonly List<bool> rawBits = new(MaximumRawBits);
    private bool hasPreviousLevel;
    private bool previousLevel;
    private byte shiftRegister;
    private int shiftCount;
    private bool inFrame;
    private long rejectedFrames;
    private long flagCount;
    private long frameCandidateCount;
    private long validFrames;

    public long RejectedFrames => rejectedFrames;
    public long FlagCount => flagCount;
    public long FrameCandidateCount => frameCandidateCount;
    public long ValidFrames => validFrames;

    public void Reset()
    {
        rawBits.Clear();
        hasPreviousLevel = false;
        previousLevel = false;
        shiftRegister = 0;
        shiftCount = 0;
        inFrame = false;
        rejectedFrames = 0;
        flagCount = 0;
        frameCandidateCount = 0;
        validFrames = 0;
    }

    public byte[]? FeedLevel(bool level)
    {
        if (!hasPreviousLevel)
        {
            previousLevel = level;
            hasPreviousLevel = true;
            return null;
        }

        bool bit = level == previousLevel;
        previousLevel = level;
        shiftRegister = (byte)((shiftRegister << 1) | (bit ? 1 : 0));
        if (shiftCount < 8) shiftCount++;
        if (inFrame) rawBits.Add(bit);

        if (shiftCount >= 8 && shiftRegister == 0x7E)
        {
            flagCount++;
            byte[]? completed = null;
            if (inFrame && rawBits.Count > 8)
            {
                rawBits.RemoveRange(rawBits.Count - 8, 8);
                completed = DecodeFrame(rawBits);
                if (rawBits.Count >= 24)
                {
                    frameCandidateCount++;
                    if (completed is null) rejectedFrames++;
                    else validFrames++;
                }
            }
            rawBits.Clear();
            inFrame = true;
            return completed;
        }

        if (rawBits.Count > MaximumRawBits)
        {
            rawBits.Clear();
            inFrame = false;
        }
        return null;
    }

    private static byte[]? DecodeFrame(IReadOnlyList<bool> bits)
    {
        var unstuffed = new List<bool>(bits.Count);
        int ones = 0;
        foreach (bool bit in bits)
        {
            if (bit)
            {
                ones++;
                if (ones > 5) return null;
                unstuffed.Add(true);
            }
            else
            {
                if (ones == 5)
                {
                    ones = 0;
                    continue;
                }
                ones = 0;
                unstuffed.Add(false);
            }
        }

        if (unstuffed.Count < 24 || unstuffed.Count % 8 != 0) return null;
        var bytes = new byte[unstuffed.Count / 8];
        for (int index = 0; index < unstuffed.Count; index++)
            if (unstuffed[index]) bytes[index / 8] |= (byte)(1 << (index % 8));
        return AisCrc.IsValid(bytes) ? bytes[..^2] : null;
    }
}
