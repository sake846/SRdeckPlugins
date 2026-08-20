namespace SRdeckPlugin.AdsB.Protocols;

public static class ModeSCrc
{
    private const uint Polynomial = 0xFFF409;

    public static uint ComputeSyndrome(ReadOnlySpan<byte> message)
    {
        uint remainder = 0;
        foreach (byte value in message)
        {
            remainder ^= (uint)value << 16;
            for (int bit = 0; bit < 8; bit++)
                remainder = (remainder & 0x800000) != 0
                    ? ((remainder << 1) ^ Polynomial) & 0xFFFFFF
                    : (remainder << 1) & 0xFFFFFF;
        }
        return remainder;
    }

    public static bool IsValidExtendedSquitter(ReadOnlySpan<byte> message) =>
        message.Length == 14 && message[0] >> 3 is 17 or 18 && ComputeSyndrome(message) == 0;

    public static bool TryValidateOrCorrectExtendedSquitter(byte[] message,
        ReadOnlySpan<float> bitConfidence, out bool corrected)
    {
        corrected = false;
        if (IsValidExtendedSquitter(message)) return true;
        if (message.Length != 14 || bitConfidence.Length != 112) return false;

        Span<int> candidateStorage = stackalloc int[8];
        int candidateCount = 0;
        for (int bit = 0; bit < 112; bit++)
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
            int byteIndex = bit / 8;
            byte mask = (byte)(1 << (7 - bit % 8));
            message[byteIndex] ^= mask;
            bool valid = IsValidExtendedSquitter(message);
            message[byteIndex] ^= mask;
            if (!valid) continue;
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
                message[firstBit / 8] ^= (byte)(1 << (7 - firstBit % 8));
                message[secondBit / 8] ^= (byte)(1 << (7 - secondBit % 8));
                bool valid = IsValidExtendedSquitter(message);
                message[firstBit / 8] ^= (byte)(1 << (7 - firstBit % 8));
                message[secondBit / 8] ^= (byte)(1 << (7 - secondBit % 8));
                if (!valid) continue;
                if (correctedPair is not null) return false;
                correctedPair = (firstBit, secondBit);
            }
            if (correctedPair is null) return false;
            message[correctedPair.Value.First / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.First % 8));
            message[correctedPair.Value.Second / 8] ^=
                (byte)(1 << (7 - correctedPair.Value.Second % 8));
            corrected = true;
            return true;
        }
        message[correctedBit / 8] ^= (byte)(1 << (7 - correctedBit % 8));
        corrected = true;
        return true;
    }
}
