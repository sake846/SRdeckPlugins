namespace SRdeckPlugin.OneSeg.Transport;

/// <summary>
/// Detects the ARIB AAC coding mode from ADTS fixed headers. ARIB STD-B32
/// assigns channel_configuration 0 to dual mono, 1 to mono, and 2 to stereo.
/// </summary>
internal sealed class AdtsAudioModeDetector
{
    // AAC payload can coincidentally contain a seven-byte ADTS-like pattern.
    // Require the same new layout twice while allowing an established layout
    // to cancel a candidate, so transient payload bytes cannot restart audio.
    private const int ConfirmationCount = 2;
    private readonly List<byte> pending = [];
    private byte? confirmedComponentType;
    private byte? candidateComponentType;
    private int candidateCount;

    public byte? Push(ReadOnlySpan<byte> value)
    {
        if (!value.IsEmpty) pending.AddRange(value.ToArray());
        byte? componentType = DetectComponentType(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
        if (componentType is byte detected)
        {
            pending.Clear();
            if (confirmedComponentType == detected)
            {
                candidateComponentType = null;
                candidateCount = 0;
                return null;
            }

            if (candidateComponentType == detected) candidateCount++;
            else
            {
                candidateComponentType = detected;
                candidateCount = 1;
            }

            if (candidateCount < ConfirmationCount) return null;
            confirmedComponentType = detected;
            candidateComponentType = null;
            candidateCount = 0;
            return detected;
        }

        if (pending.Count > 6) pending.RemoveRange(0, pending.Count - 6);
        return null;
    }

    public void Reset()
    {
        pending.Clear();
        confirmedComponentType = null;
        candidateComponentType = null;
        candidateCount = 0;
    }

    internal static byte? DetectComponentType(ReadOnlySpan<byte> value)
    {
        for (int offset = 0; offset + 7 <= value.Length; offset++)
        {
            // syncword=0xfff, layer=00; ID and protection_absent may vary.
            if (value[offset] != 0xff || (value[offset + 1] & 0xf6) != 0xf0) continue;
            int samplingFrequencyIndex = (value[offset + 2] >> 2) & 0x0f;
            if (samplingFrequencyIndex == 0x0f) continue;
            int frameLength = ((value[offset + 3] & 0x03) << 11) |
                (value[offset + 4] << 3) | (value[offset + 5] >> 5);
            int headerLength = (value[offset + 1] & 1) != 0 ? 7 : 9;
            if (frameLength < headerLength) continue;

            int channelConfiguration = ((value[offset + 2] & 1) << 2) |
                (value[offset + 3] >> 6);
            return channelConfiguration switch
            {
                0 => 0x02, // two SCEs without a PCE: dual mono in ARIB broadcasting
                1 => 0x01,
                2 => 0x03,
                _ => null
            };
        }
        return null;
    }
}
