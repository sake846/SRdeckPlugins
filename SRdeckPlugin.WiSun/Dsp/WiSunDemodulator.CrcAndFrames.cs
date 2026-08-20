using SRdeckPlugin.Contracts;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.Dsp;

/// <summary>
/// Streaming IEEE 802.15.4 SUN-FSK receiver for JP FAN mode #1b and HAN/B Route.
/// The input must be a host-channelized complex baseband stream with 8 samples/bit.
/// </summary>
public sealed partial class WiSunDemodulator
{
    private static void ApplyPn9(Span<byte> data)
    {
        int state = Pn9Seed;
        for (int byteIndex = 0; byteIndex < data.Length; byteIndex++)
        {
            byte value = data[byteIndex];
            for (int bit = 0; bit < 8; bit++)
            {
                value ^= (byte)((state & 1) << bit);
                int feedback = ((state >> 0) ^ (state >> 5)) & 1;
                state = (state >> 1) | (feedback << 8);
            }
            data[byteIndex] = value;
        }
    }


    private static void ReadFcsValues(
        ReadOnlySpan<byte> psdu,
        int fcsLength,
        out ulong received,
        out ulong calculated)
    {
        ReadOnlySpan<byte> payload = psdu[..^fcsLength];
        ReadOnlySpan<byte> receivedBytes = psdu[^fcsLength..];
        if (fcsLength == 2)
        {
            calculated = ComputeCrc16(payload);
            received = (ushort)(receivedBytes[0] | receivedBytes[1] << 8);
            return;
        }
        calculated = ComputeCrc32(payload);
        received = (uint)(receivedBytes[0] | receivedBytes[1] << 8 |
            receivedBytes[2] << 16 | receivedBytes[3] << 24);
    }

    private static string FormatFcs(ulong value, int fcsLength) =>
        $"0x{value.ToString($"X{fcsLength * 2}")}";

    private static readonly ushort[] Crc16Table = PrecomputeCrc16Table();
    private static readonly uint[] Crc32Table = PrecomputeCrc32Table();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float FastAtan2(float y, float x)
    {
        if (x == 0f && y == 0f) return 0f;
        float absY = MathF.Abs(y);
        float absX = MathF.Abs(x);
        float minVal = MathF.Min(absX, absY);
        float maxVal = MathF.Max(absX, absY);
        float a = minVal / maxVal;
        float s = a * a;
        float r = ((-0.0464964749f * s + 0.15931422f) * s - 0.327622764f) * s * a + a;
        if (absY > absX) r = 1.57079632679f - r;
        if (x < 0f) r = 3.14159265359f - r;
        if (y < 0f) r = -r;
        return r;
    }

    private static ushort[] PrecomputeCrc16Table()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0) crc = (ushort)((crc >> 1) ^ 0x8408);
                else crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    private static uint[] PrecomputeCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0) crc = (crc >> 1) ^ 0xEDB88320u;
                else crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = (ushort)((crc >> 8) ^ Crc16Table[(crc ^ b) & 0xFF]);
        }
        return crc;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        }

        // IEEE 802.15.4g pads the CRC-32 input with zero octets to a
        // four-octet boundary. These padding octets are not transmitted.
        int paddingBytes = (4 - (data.Length & 3)) & 3;
        for (int byteIndex = 0; byteIndex < paddingBytes; byteIndex++)
        {
            crc = (crc >> 8) ^ Crc32Table[crc & 0xFF];
        }
        return ~crc;
    }

    private void MeasurePower(int start, int end, out float peakDbfs, out float averageDbfs)
    {
        float peak = 0;
        double sum = 0;
        for (int index = start; index < end; index++)
        {
            peak = MathF.Max(peak, power[index]);
            sum += power[index];
        }
        peakDbfs = 10 * MathF.Log10(MathF.Max(peak, 1e-12f));
        averageDbfs = 10 * MathF.Log10(MathF.Max((float)(sum / Math.Max(1, end - start)), 1e-12f));
    }

    private void TrimConsumedSamples()
    {
        int retain = (MaximumCapturedPreambleBytes * 8 + SfdBitCount + PhrBitCount) *
            SamplesPerBit;
        int remove = Math.Max(0, scanSample - retain);
        if (remove < 32768) return;
        discriminator.RemoveRange(0, remove);
        power.RemoveRange(0, remove);
        discriminatorPrefix.RemoveRange(0, remove);
        powerPrefix.RemoveRange(0, remove);
        bufferSampleOffset += remove;
        scanSample -= remove;
        if (rfBurstStartSample >= 0)
            rfBurstStartSample = Math.Max(0, rfBurstStartSample - remove);
    }

    private static WiSunPacketFrame DecodeMacFrame(
        byte[] macFrame,
        DateTimeOffset timestamp,
        long frequencyHz,
        double durationMs,
        float peakDbfs,
        float snrDb,
        bool whiteningEnabled,
        int fcsLength,
        bool crcValid,
        string phyName)
    {
        string frameType = "IEEE 802.15.4g SUN";
        byte? sequenceNumber = null;
        ushort? panId = null;
        string? sourceAddress = null;
        string? destinationAddress = null;
        if (macFrame.Length >= 3)
        {
            ushort frameControl = (ushort)(macFrame[0] | macFrame[1] << 8);
            sequenceNumber = macFrame[2];
            frameType = (frameControl & 0x07) switch
            {
                0 => "Beacon",
                1 => "Data",
                2 => "ACK",
                3 => "MAC Command",
                _ => "802.15.4g Frame"
            };
            int destinationMode = frameControl >> 10 & 0x03;
            int sourceMode = frameControl >> 14 & 0x03;
            bool panCompression = (frameControl & 0x0040) != 0;
            int offset = 3;
            if (destinationMode != 0 && offset + 2 <= macFrame.Length)
            {
                panId = (ushort)(macFrame[offset] | macFrame[offset + 1] << 8);
                offset += 2;
                destinationAddress = ReadAddress(macFrame, ref offset, destinationMode);
            }
            if (sourceMode != 0)
            {
                if (!panCompression && offset + 2 <= macFrame.Length)
                {
                    panId ??= (ushort)(macFrame[offset] | macFrame[offset + 1] << 8);
                    offset += 2;
                }
                sourceAddress = ReadAddress(macFrame, ref offset, sourceMode);
            }
        }

        return new WiSunPacketFrame
        {
            Timestamp = timestamp,
            FrequencyHz = frequencyHz,
            DurationMs = durationMs,
            PeakDbfs = peakDbfs,
            SnrDb = snrDb,
            RawPayload = macFrame,
            FrameType = frameType,
            SequenceNumber = sequenceNumber,
            PanId = panId,
            SrcAddress = sourceAddress,
            DstAddress = destinationAddress,
            FrameLengthBytes = macFrame.Length,
            CrcValid = crcValid,
            ProtocolSummary = $"[Wi-SUN {phyName}] {frameType} | Seq:{sequenceNumber?.ToString() ?? "--"} | " +
                $"Len:{macFrame.Length}B | PN9:{(whiteningEnabled ? "on" : "off")} | " +
                $"FCS:{fcsLength * 8} | CRC:{(crcValid ? "OK" : "NG")}"
        };
    }

    private static string? ReadAddress(byte[] frame, ref int offset, int mode)
    {
        int length = mode == 2 ? 2 : mode == 3 ? 8 : 0;
        if (length == 0 || offset + length > frame.Length) return null;
        byte[] address = frame.AsSpan(offset, length).ToArray();
        Array.Reverse(address);
        offset += length;
        return $"0x{Convert.ToHexString(address)}";
    }
}
