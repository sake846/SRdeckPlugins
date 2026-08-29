namespace SRdeckPlugin.Ais.Protocols;

internal sealed class AisHdlcDecoder
{
    private const int MaximumRawBits = 2048;
    private readonly ulong[] rawBits = new ulong[(MaximumRawBits + 1 + 63) / 64];
    private readonly byte[] decodedBytes = new byte[(MaximumRawBits + 7) / 8];
    private int rawBitCount;
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
        ResetState();
        rejectedFrames = 0;
        flagCount = 0;
        frameCandidateCount = 0;
        validFrames = 0;
    }

    public void ResetState()
    {
        rawBitCount = 0;
        hasPreviousLevel = false;
        previousLevel = false;
        shiftRegister = 0;
        shiftCount = 0;
        inFrame = false;
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
        if (inFrame) AppendRawBit(bit);

        if (shiftCount >= 8 && shiftRegister == 0x7E)
        {
            flagCount++;
            byte[]? completed = null;
            if (inFrame && rawBitCount > 8)
            {
                int frameBitCount = rawBitCount - 8;
                completed = DecodeFrame(frameBitCount);
                if (frameBitCount >= 24)
                {
                    frameCandidateCount++;
                    if (completed is null) rejectedFrames++;
                    else validFrames++;
                }
            }
            rawBitCount = 0;
            inFrame = true;
            return completed;
        }

        if (rawBitCount > MaximumRawBits)
        {
            rawBitCount = 0;
            inFrame = false;
        }
        return null;
    }

    private void AppendRawBit(bool bit)
    {
        int index = rawBitCount++;
        ref ulong word = ref rawBits[index >> 6];
        ulong mask = 1UL << (index & 63);
        if (bit) word |= mask;
        else word &= ~mask;
    }

    private bool GetRawBit(int index) =>
        (rawBits[index >> 6] & (1UL << (index & 63))) != 0;

    private byte[]? DecodeFrame(int bitCount)
    {
        decodedBytes.AsSpan(0, (bitCount + 7) / 8).Clear();
        int ones = 0;
        int outputBitCount = 0;
        for (int index = 0; index < bitCount; index++)
        {
            bool bit = GetRawBit(index);
            if (bit)
            {
                ones++;
                if (ones > 5) return null;
                decodedBytes[outputBitCount >> 3] |=
                    (byte)(1 << (outputBitCount & 7));
                outputBitCount++;
            }
            else
            {
                if (ones == 5)
                {
                    ones = 0;
                    continue;
                }
                ones = 0;
                outputBitCount++;
            }
        }

        if (outputBitCount < 24 || outputBitCount % 8 != 0) return null;
        int byteCount = outputBitCount / 8;
        ReadOnlySpan<byte> frame = decodedBytes.AsSpan(0, byteCount);
        return AisCrc.IsValid(frame) ? frame[..^2].ToArray() : null;
    }
}
