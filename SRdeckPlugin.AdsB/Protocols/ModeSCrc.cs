namespace SRdeckPlugin.AdsB.Protocols;

public static class ModeSCrc
{
    private const uint Polynomial = 0xFFF409;
    private const int ExtendedSquitterLength = 14;
    private const int ExtendedSquitterBits = ExtendedSquitterLength * 8;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly uint[] BitSyndromes = CreateBitSyndromes();

    public static uint ComputeSyndrome(ReadOnlySpan<byte> message)
    {
        uint remainder = 0;
        foreach (byte value in message)
        {
            int tableIndex = (int)(((remainder >> 16) ^ value) & 0xFF);
            remainder = ((remainder << 8) & 0xFFFFFF) ^ CrcTable[tableIndex];
        }
        return remainder;
    }

    public static bool IsValidExtendedSquitter(ReadOnlySpan<byte> message) =>
        message.Length == 14 && message[0] >> 3 is 17 or 18 && ComputeSyndrome(message) == 0;

    public static bool TryValidateOrCorrectExtendedSquitter(byte[]? message,
        ReadOnlySpan<float> bitConfidence, out bool corrected)
    {
        if (message is null)
        {
            corrected = false;
            return false;
        }
        return TryValidateOrCorrectExtendedSquitter(message.AsSpan(), bitConfidence, out corrected);
    }

    public static bool TryValidateOrCorrectExtendedSquitter(Span<byte> message,
        ReadOnlySpan<float> bitConfidence, out bool corrected)
    {
        corrected = false;
        if (IsValidExtendedSquitter(message)) return true;
        if (message.Length != ExtendedSquitterLength || bitConfidence.Length != ExtendedSquitterBits)
            return false;

        uint receivedSyndrome = ComputeSyndrome(message);

        foreach (float confidence in bitConfidence)
            if (!float.IsFinite(confidence) || confidence < 0 || confidence > 1)
                return false;

        Span<int> candidateStorage = stackalloc int[8];
        int candidateCount = 0;
        for (int bit = 0; bit < ExtendedSquitterBits; bit++)
        {
            if (bitConfidence[bit] >= 0.30f) continue;
            int insert = candidateCount;
            while (insert > 0 && bitConfidence[candidateStorage[insert - 1]] > bitConfidence[bit]) insert--;
            int limit = Math.Min(candidateCount, candidateStorage.Length - 1);
            for (int move = limit; move > insert; move--) candidateStorage[move] = candidateStorage[move - 1];
            if (insert < candidateStorage.Length) candidateStorage[insert] = bit;
            if (candidateCount < candidateStorage.Length) candidateCount++;
        }
        ReadOnlySpan<int> candidates = candidateStorage[..candidateCount];
        int correctedBit = -1;
        foreach (int bit in candidates)
        {
            if (BitSyndromes[bit] != receivedSyndrome) continue;
            if (correctedBit >= 0) return false;
            correctedBit = bit;
        }
        if (correctedBit < 0)
        {
            (int First, int Second)? correctedPair = null;
            int pairCandidateCount = 0;
            while (pairCandidateCount < candidates.Length && bitConfidence[candidates[pairCandidateCount]] < 0.18f)
                pairCandidateCount++;
            for (int first = 0; first < pairCandidateCount; first++)
            for (int second = first + 1; second < pairCandidateCount; second++)
            {
                int firstBit = candidates[first];
                int secondBit = candidates[second];
                if (bitConfidence[firstBit] + bitConfidence[secondBit] >= 0.30f) continue;
                if ((BitSyndromes[firstBit] ^ BitSyndromes[secondBit]) != receivedSyndrome) continue;
                if (correctedPair is not null) return false;
                correctedPair = (firstBit, secondBit);
            }
            if (correctedPair is null) return false;
            message[correctedPair.Value.First / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.First % 8));
            message[correctedPair.Value.Second / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.Second % 8));
            if (IsValidExtendedSquitter(message))
            {
                corrected = true;
                return true;
            }
            message[correctedPair.Value.First / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.First % 8));
            message[correctedPair.Value.Second / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.Second % 8));
            return false;
        }
        message[correctedBit / 8] ^= (byte)(1 << (7 - correctedBit % 8));
        if (IsValidExtendedSquitter(message))
        {
            corrected = true;
            return true;
        }
        message[correctedBit / 8] ^= (byte)(1 << (7 - correctedBit % 8));
        return false;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (int value = 0; value < table.Length; value++)
        {
            uint remainder = (uint)value << 16;
            for (int bit = 0; bit < 8; bit++)
                remainder = (remainder & 0x800000) != 0
                    ? ((remainder << 1) ^ Polynomial) & 0xFFFFFF
                    : (remainder << 1) & 0xFFFFFF;
            table[value] = remainder;
        }
        return table;
    }

    private static uint[] CreateBitSyndromes()
    {
        var syndromes = new uint[ExtendedSquitterBits];
        Span<byte> message = stackalloc byte[ExtendedSquitterLength];
        for (int bit = 0; bit < syndromes.Length; bit++)
        {
            message.Clear();
            message[bit / 8] = (byte)(1 << (7 - bit % 8));
            syndromes[bit] = ComputeSyndrome(message);
        }
        return syndromes;
    }
}
