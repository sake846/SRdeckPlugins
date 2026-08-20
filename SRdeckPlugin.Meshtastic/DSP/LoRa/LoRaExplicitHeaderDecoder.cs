using System;
using System.Collections.Generic;
using System.Numerics;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public sealed record LoRaExplicitHeader(
    DateTimeOffset DecodedAt,
    int PayloadLength,
    int CodingRateDenominator,
    bool HasPayloadCrc,
    int HeaderChecksum,
    int CalculatedChecksum,
    bool IsChecksumValid,
    int CorrectedCodewords,
    IReadOnlyList<byte> InitialPayloadNibbles);

/// <summary>
/// Decoder for the first LoRa explicit-header block. Input is the eight
/// reduced-rate, Gray-mapped SF-2 symbols produced by the demodulator.
/// </summary>
internal static class LoRaExplicitHeaderDecoder
{
    private const int HeaderSymbolCount = 8;

    // Complete LoRa Hamming(8,4) codebook. The high nibble is in LoRa's
    // transmitted bit order and is reversed after nearest-codeword selection.
    private static readonly byte[] Hamming84Codewords =
    [
        0x00, 0x17, 0x2D, 0x3A, 0x4E, 0x59, 0x63, 0x74,
        0x8B, 0x9C, 0xA6, 0xB1, 0xC5, 0xD2, 0xE8, 0xFF
    ];

    public static LoRaExplicitHeader Decode(IReadOnlyList<ushort> symbols, int spreadingFactor)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count != HeaderSymbolCount)
        {
            throw new ArgumentException($"An explicit header requires exactly {HeaderSymbolCount} symbols.", nameof(symbols));
        }

        int headerCodewordCount = spreadingFactor - 2;
        if (headerCodewordCount < 5) throw new ArgumentOutOfRangeException(nameof(spreadingFactor));
        byte[] codewords = Deinterleave(symbols, headerCodewordCount);
        Span<byte> nibbles = stackalloc byte[headerCodewordCount];
        int correctedCodewords = 0;
        for (int i = 0; i < codewords.Length; i++)
        {
            nibbles[i] = DecodeHamming84(codewords[i], out bool corrected);
            if (corrected) correctedCodewords++;
        }

        int payloadLength = (nibbles[0] << 4) | nibbles[1];
        int codingRateValue = nibbles[2] >> 1;
        bool hasPayloadCrc = (nibbles[2] & 1) != 0;
        int headerChecksum = ((nibbles[3] & 1) << 4) | nibbles[4];
        int calculatedChecksum = CalculateChecksum(nibbles[0], nibbles[1], nibbles[2]);
        bool fieldsValid = payloadLength is > 0 and <= 255 && codingRateValue is >= 1 and <= 4;

        return new LoRaExplicitHeader(
            DateTimeOffset.UtcNow,
            payloadLength,
            codingRateValue + 4,
            hasPayloadCrc,
            headerChecksum,
            calculatedChecksum,
            fieldsValid && headerChecksum == calculatedChecksum,
            correctedCodewords,
            nibbles[5..].ToArray());
    }

    public static int CalculateChecksum(int lengthHighNibble, int lengthLowNibble, int flagsNibble)
    {
        int h0 = lengthHighNibble & 0x0F;
        int h1 = lengthLowNibble & 0x0F;
        int h2 = flagsNibble & 0x0F;

        int c4 = Bit(h0, 3) ^ Bit(h0, 2) ^ Bit(h0, 1) ^ Bit(h0, 0);
        int c3 = Bit(h0, 3) ^ Bit(h1, 3) ^ Bit(h1, 2) ^ Bit(h1, 1) ^ Bit(h2, 0);
        int c2 = Bit(h0, 2) ^ Bit(h1, 3) ^ Bit(h1, 0) ^ Bit(h2, 3) ^ Bit(h2, 1);
        int c1 = Bit(h0, 1) ^ Bit(h1, 2) ^ Bit(h1, 0) ^ Bit(h2, 2) ^ Bit(h2, 1) ^ Bit(h2, 0);
        int c0 = Bit(h0, 0) ^ Bit(h1, 1) ^ Bit(h2, 3) ^ Bit(h2, 2) ^ Bit(h2, 1) ^ Bit(h2, 0);
        return (c4 << 4) | (c3 << 3) | (c2 << 2) | (c1 << 1) | c0;
    }

    private static byte[] Deinterleave(IReadOnlyList<ushort> symbols, int headerCodewordCount)
    {
        var codewords = new byte[headerCodewordCount];
        for (int symbolIndex = 0; symbolIndex < HeaderSymbolCount; symbolIndex++)
        {
            int symbol = symbols[symbolIndex] & ((1 << headerCodewordCount) - 1);
            for (int bitIndex = 0; bitIndex < headerCodewordCount; bitIndex++)
            {
                int bit = (symbol >> (headerCodewordCount - 1 - bitIndex)) & 1;
                int codewordIndex = Mod(symbolIndex - bitIndex - 1, headerCodewordCount);
                codewords[codewordIndex] |= (byte)(bit << (HeaderSymbolCount - 1 - symbolIndex));
            }
        }
        return codewords;
    }

    private static byte DecodeHamming84(byte received, out bool corrected)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < Hamming84Codewords.Length; i++)
        {
            int distance = BitOperations.PopCount((uint)(received ^ Hamming84Codewords[i]));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = i;
        }

        corrected = bestDistance == 1;
        return ReverseNibble(bestIndex);
    }

    private static byte ReverseNibble(int value) => (byte)(
        ((value & 0x1) << 3) |
        ((value & 0x2) << 1) |
        ((value & 0x4) >> 1) |
        ((value & 0x8) >> 3));

    private static int Bit(int value, int bit) => (value >> bit) & 1;

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
