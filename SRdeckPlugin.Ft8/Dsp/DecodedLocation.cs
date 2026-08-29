using System.Buffers.Binary;

namespace SRdeckPlugin.Ft8.Dsp;

internal readonly record struct PayloadKey(ulong High, ushort Low)
{
    public static PayloadKey FromPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new ArgumentException("An FT8/FT4 payload contains 10 bytes.", nameof(payload));
        return new PayloadKey(
            BinaryPrimitives.ReadUInt64BigEndian(payload),
            BinaryPrimitives.ReadUInt16BigEndian(payload[8..]));
    }
}

internal readonly record struct DecodedLocation(
    PayloadKey Payload,
    int AudioFrequency,
    double TimeOffset);

internal static class DecodedLocationDeduplicator
{
    public static bool ContainsNearby(
        List<DecodedLocation> locations,
        PayloadKey payload,
        int audioFrequency,
        double timeOffset,
        int frequencyTolerance,
        double timeTolerance)
    {
        for (int index = 0; index < locations.Count; index++)
        {
            DecodedLocation item = locations[index];
            if (item.Payload != payload ||
                Math.Abs(item.AudioFrequency - audioFrequency) > frequencyTolerance ||
                Math.Abs(item.TimeOffset - timeOffset) > timeTolerance)
                continue;
            return true;
        }
        return false;
    }
}
