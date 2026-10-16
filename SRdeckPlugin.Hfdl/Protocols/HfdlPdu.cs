namespace SRdeckPlugin.Hfdl.Protocols;

internal static class HfdlPdu
{
    // MPDU FCS covers only the header; LPDU lengths include each LPDU's own FCS.
    public static bool TryReadLengths(ReadOnlySpan<byte> bytes, out int headerLength, out int frameLength)
    {
        headerLength = frameLength = 0;
        if (bytes.Length < 2) return false;
        if ((bytes[0] & 1) == 0)
        {
            headerLength = 64;
            frameLength = 66;
            return bytes.Length >= frameLength && HfdlCrc.IsValid(bytes[..frameLength]);
        }

        int payloadLength = 0;
        if ((bytes[0] & 2) != 0)
        {
            int count = (bytes[0] >> 2) & 15;
            headerLength = 6 + count;
            if (bytes.Length < headerLength + 2) return false;
            for (int index = 6; index < headerLength; index++)
            {
                if (bytes[index] < 2) return false;
                payloadLength += bytes[index] + 1;
            }
        }
        else
        {
            headerLength = 2;
            int aircraftCount = ((bytes[0] >> 4) & 7) + 1;
            for (int aircraft = 0; aircraft < aircraftCount; aircraft++)
            {
                if (bytes.Length < headerLength + 2) return false;
                int count = bytes[headerLength + 1] >> 4;
                int lengthsStart = headerLength + 2;
                headerLength = lengthsStart + count;
                if (bytes.Length < headerLength + 2) return false;
                for (int index = lengthsStart; index < headerLength; index++)
                {
                    if (bytes[index] < 2) return false;
                    payloadLength += bytes[index] + 1;
                }
            }
        }
        frameLength = headerLength + 2 + payloadLength;
        return HfdlCrc.IsValid(bytes[..(headerLength + 2)]);
    }
}
