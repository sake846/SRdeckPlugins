using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class TmccDecoder
{
    private const int MaximumCorrectableErrors = 8;
    private const int MaximumSynchronizationErrors = 3;
    private static readonly byte[] Sync0 = [0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0];
    private static readonly byte[] Sync1 = [1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1];
    // ARIB STD-B31 TMCC shortened (273,191) difference-set cyclic-code
    // parity-check polynomial. The
    // transmitted 184-bit payload is preceded by 89 zero bits before checking.
    private static readonly int[] ParityCheckOneIndexes =
    [
        0, 4, 8, 10, 12, 16, 22, 24, 28, 34, 44, 46, 48, 52, 54, 62,
        64, 66, 67, 68, 70, 71, 75, 76, 77, 78, 79, 82, 83, 85, 88, 93,
        95, 97, 98, 99, 101, 102, 103, 106, 108, 109, 110, 111, 112, 114,
        119, 120, 121, 122, 123, 125, 127, 128, 130, 133, 134, 135, 139, 140,
        145, 150, 154, 155, 158, 160, 162, 165, 167, 168, 169, 170, 171, 173,
        174, 179, 180, 181, 185, 186, 191
    ];
    // A (273,17,1) perfect difference set supplies 17 parity checks that are
    // orthogonal on each codeword bit. A strict majority therefore identifies
    // every error pattern of weight eight or less.
    private static readonly int[] MajorityCheckOffsets =
    [
        0, 18, 24, 46, 50, 67, 103, 112, 115,
        126, 128, 159, 166, 167, 186, 196, 201
    ];

    private readonly byte[] bits = new byte[IsdbTParameters.SymbolsPerFrame];
    private ComplexF[] previous = [];
    private int writeIndex;
    private int count;
    private int sinceFrame = IsdbTParameters.SymbolsPerFrame;

    public bool IsLocked { get; private set; }
    public long ValidFrames { get; private set; }
    internal long SyncMatches { get; private set; }
    internal long ParityFailures { get; private set; }
    internal int MinimumSyncErrors { get; private set; } = 16;
    public TmccParameters? Parameters { get; private set; }

    public void Reset()
    {
        Array.Clear(bits);
        previous = [];
        writeIndex = 0;
        count = 0;
        sinceFrame = IsdbTParameters.SymbolsPerFrame;
        IsLocked = false;
        ValidFrames = 0;
        SyncMatches = 0;
        ParityFailures = 0;
        MinimumSyncErrors = 16;
        Parameters = null;
    }

    public void Resynchronize()
    {
        previous = [];
        writeIndex = 0;
        count = 0;
        sinceFrame = IsdbTParameters.SymbolsPerFrame;
    }

    public bool ProcessSymbol(ReadOnlySpan<ComplexF> equalized, IsdbTMode mode)
    {
        ReadOnlySpan<int> carriers = IsdbTParameters.TmccCarriers(mode);
        if (previous.Length != carriers.Length)
        {
            previous = new ComplexF[carriers.Length];
            for (int index = 0; index < carriers.Length; index++)
                previous[index] = equalized[carriers[index]];
            return false;
        }

        int vote = 0;
        for (int index = 0; index < carriers.Length; index++)
        {
            ComplexF current = equalized[carriers[index]];
            ComplexF difference = current * previous[index].Conjugate();
            vote += difference.Real >= 0 ? 1 : -1;
            previous[index] = current;
        }
        bits[writeIndex] = vote >= 0 ? (byte)0 : (byte)1;
        writeIndex = (writeIndex + 1) % bits.Length;
        count = Math.Min(bits.Length, count + 1);
        sinceFrame++;
        if (count < bits.Length || sinceFrame < bits.Length - 1) return false;

        byte[] frame = Snapshot();
        int syncErrors = SyncErrors(frame);
        MinimumSyncErrors = Math.Min(MinimumSyncErrors, syncErrors);
        if (syncErrors > MaximumSynchronizationErrors)
        {
            return false;
        }
        SyncMatches++;
        if (!TryCorrect(frame, out _))
        {
            ParityFailures++;
            return false;
        }

        sinceFrame = 0;
        IsLocked = true;
        ValidFrames++;
        Parameters = Parse(frame, mode);
        return true;
    }

    private byte[] Snapshot()
    {
        var frame = new byte[bits.Length];
        for (int index = 0; index < bits.Length; index++)
            frame[index] = bits[(writeIndex + index) % bits.Length];
        return frame;
    }

    private static int SyncErrors(ReadOnlySpan<byte> frame)
    {
        int errors0 = 0, errors1 = 0;
        for (int index = 0; index < 16; index++)
        {
            if (frame[index + 1] != Sync0[index]) errors0++;
            if (frame[index + 1] != Sync1[index]) errors1++;
        }
        return Math.Min(errors0, errors1);
    }

    internal static bool TryCorrect(Span<byte> frame, out int correctedBits)
    {
        correctedBits = 0;
        if (frame.Length != IsdbTParameters.SymbolsPerFrame) return false;
        if (HasValidParity(frame)) return true;

        // B20..B203 are the shortened codeword. Restore the 89 leading zero
        // symbols of the parent (273,191) code before majority-logic decoding.
        var received = new byte[273];
        frame[20..].CopyTo(received.AsSpan(89));
        byte[] corrected = received.ToArray();
        for (int bit = 0; bit < received.Length; bit++)
        {
            int votes = 0;
            foreach (int anchor in MajorityCheckOffsets)
            {
                byte parity = 0;
                foreach (int offset in MajorityCheckOffsets)
                {
                    int index = bit + offset - anchor;
                    if (index < 0) index += received.Length;
                    else if (index >= received.Length) index -= received.Length;
                    parity ^= received[index];
                }
                votes += parity;
            }
            if (votes <= MaximumCorrectableErrors) continue;
            corrected[bit] ^= 1;
            correctedBits++;
        }

        // A bounded-distance decoder must not accept a correction outside the
        // code's guaranteed radius or one that changes an implicit shortened bit.
        if (correctedBits > MaximumCorrectableErrors ||
            corrected.AsSpan(0, 89).ContainsAnyExcept((byte)0))
            return false;

        corrected.AsSpan(89).CopyTo(frame[20..]);
        return HasValidParity(frame);
    }

    internal static bool HasValidParity(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != IsdbTParameters.SymbolsPerFrame) return false;
        for (int syndromeIndex = 0; syndromeIndex < 82; syndromeIndex++)
        {
            byte syndrome = 0;
            foreach (int polynomialIndex in ParityCheckOneIndexes)
            {
                // In the shortened difference-set codeword, positions 0..88 are implicit zero;
                // position 89 maps to TMCC frame bit 20.
                int wordIndex = syndromeIndex + polynomialIndex;
                if (wordIndex >= 89) syndrome ^= frame[wordIndex - 69];
            }
            if (syndrome != 0) return false;
        }
        return true;
    }

    private static TmccParameters Parse(ReadOnlySpan<byte> frame, IsdbTMode mode) => new(
        frame[27] != 0,
        frame[26] != 0,
        Read(frame, 22, 4),
        ParseLayer(frame, 28, mode),
        ParseLayer(frame, 41, mode),
        ParseLayer(frame, 54, mode),
        true);

    private static TmccLayerParameters ParseLayer(ReadOnlySpan<byte> frame, int start, IsdbTMode mode)
    {
        int modulation = Read(frame, start, 3);
        int codeRate = Read(frame, start + 3, 3);
        int interleavingCode = Read(frame, start + 6, 3);
        int segments = Read(frame, start + 9, 4);
        return new(
            modulation <= 3 ? (IsdbTModulation)modulation : IsdbTModulation.Unused,
            codeRate <= 4 ? (IsdbTCodeRate)codeRate : IsdbTCodeRate.Unused,
            IsdbTParameters.TimeInterleavingLength(mode, interleavingCode),
            segments is >= 1 and <= 13 ? segments : 0);
    }

    private static int Read(ReadOnlySpan<byte> bits, int start, int length)
    {
        int value = 0;
        for (int index = 0; index < length; index++) value = (value << 1) | bits[start + index];
        return value;
    }
}
