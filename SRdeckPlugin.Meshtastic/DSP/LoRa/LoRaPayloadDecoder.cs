using System;
using System.Collections.Generic;
using System.Numerics;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public sealed record LoRaPayloadFrame(
    DateTimeOffset DecodedAt,
    byte[] Payload,
    bool HasPayloadCrc,
    bool? IsPayloadCrcValid,
    ushort? ReceivedCrc,
    ushort? CalculatedCrc,
    int CodingRateDenominator,
    int CorrectedCodewords);

internal static class LoRaPayloadDecoder
{
    private static readonly byte[] Hamming84Codewords =
    [
        0x00, 0x17, 0x2D, 0x3A, 0x4E, 0x59, 0x63, 0x74,
        0x8B, 0x9C, 0xA6, 0xB1, 0xC5, 0xD2, 0xE8, 0xFF
    ];

    public static byte[] DecodeBlock(IReadOnlyList<ushort> symbols, int codingRateDenominator, int spreadingFactor, out int correctedCodewords)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (codingRateDenominator is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(codingRateDenominator));
        }
        if (symbols.Count != codingRateDenominator)
        {
            throw new ArgumentException($"CR 4/{codingRateDenominator} requires {codingRateDenominator} symbols per block.", nameof(symbols));
        }

        int codewordCount = spreadingFactor;
        byte[] codewords = Deinterleave(symbols, codewordCount, codingRateDenominator);
        var nibbles = new byte[codewordCount];
        correctedCodewords = 0;
        for (int i = 0; i < codewords.Length; i++)
        {
            nibbles[i] = DecodeCodeword(codewords[i], codingRateDenominator, out bool corrected);
            if (corrected) correctedCodewords++;
        }
        return nibbles;
    }

    public static LoRaPayloadFrame BuildFrame(LoRaExplicitHeader header, IReadOnlyList<byte> decodedNibbles, int correctedCodewords)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(decodedNibbles);
        int crcByteCount = header.HasPayloadCrc ? 2 : 0;
        int encodedByteCount = header.PayloadLength + crcByteCount;
        if (decodedNibbles.Count < encodedByteCount * 2)
        {
            throw new ArgumentException("Not enough decoded nibbles for the declared payload.", nameof(decodedNibbles));
        }

        var payload = new byte[header.PayloadLength];
        var receivedBytes = new byte[encodedByteCount];
        for (int i = 0; i < encodedByteCount; i++)
        {
            receivedBytes[i] = (byte)((decodedNibbles[(i * 2) + 1] << 4) | decodedNibbles[i * 2]);
            if (i < payload.Length)
            {
                payload[i] = (byte)(receivedBytes[i] ^ GetWhiteningByte(i));
            }
        }

        ushort? receivedCrc = null;
        ushort? calculatedCrc = null;
        bool? isCrcValid = null;
        if (header.HasPayloadCrc)
        {
            receivedCrc = (ushort)(receivedBytes[header.PayloadLength] | (receivedBytes[header.PayloadLength + 1] << 8));
            if (payload.Length >= 2)
            {
                ushort crc = CalculateCrc(payload.AsSpan(0, payload.Length - 2));
                calculatedCrc = (ushort)(crc ^ payload[^1] ^ (payload[^2] << 8));
                isCrcValid = receivedCrc == calculatedCrc;
            }
            else
            {
                isCrcValid = false;
            }
        }

        return new LoRaPayloadFrame(
            DateTimeOffset.UtcNow,
            payload,
            header.HasPayloadCrc,
            isCrcValid,
            receivedCrc,
            calculatedCrc,
            header.CodingRateDenominator,
            correctedCodewords);
    }

    public static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte value in data)
        {
            byte next = value;
            for (int bit = 0; bit < 8; bit++)
            {
                bool feedback = ((crc & 0x8000) != 0) ^ ((next & 0x80) != 0);
                crc = (ushort)(crc << 1);
                if (feedback) crc ^= 0x1021;
                next <<= 1;
            }
        }
        return crc;
    }

    public static byte GetWhiteningByte(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        byte state = 0xFF;
        for (int i = 0; i < index; i++)
        {
            int feedback = ((state >> 7) ^ (state >> 5) ^ (state >> 4) ^ (state >> 3)) & 1;
            state = (byte)((state << 1) | feedback);
        }
        return state;
    }

    private static byte[] Deinterleave(IReadOnlyList<ushort> symbols, int codewordCount, int codewordLength)
    {
        var codewords = new byte[codewordCount];
        for (int symbolIndex = 0; symbolIndex < codewordLength; symbolIndex++)
        {
            int symbol = symbols[symbolIndex] & ((1 << codewordCount) - 1);
            for (int bitIndex = 0; bitIndex < codewordCount; bitIndex++)
            {
                int bit = (symbol >> (codewordCount - 1 - bitIndex)) & 1;
                int codewordIndex = Mod(symbolIndex - bitIndex - 1, codewordCount);
                codewords[codewordIndex] |= (byte)(bit << (codewordLength - 1 - symbolIndex));
            }
        }
        return codewords;
    }

    private static byte DecodeCodeword(byte received, int codingRateDenominator, out bool corrected)
    {
        int bestNibble = 0;
        int bestDistance = int.MaxValue;
        for (int nibble = 0; nibble < 16; nibble++)
        {
            byte encoded = EncodeCodeword(nibble, codingRateDenominator);
            int distance = BitOperations.PopCount((uint)(received ^ encoded));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestNibble = nibble;
        }

        corrected = bestDistance == 1 && codingRateDenominator >= 7;
        return (byte)bestNibble;
    }

    private static byte EncodeCodeword(int nibble, int codingRateDenominator)
    {
        int reversed = ReverseNibble(nibble);
        if (codingRateDenominator == 5)
        {
            int parity = BitOperations.PopCount((uint)nibble) & 1;
            return (byte)((reversed << 1) | parity);
        }
        return (byte)(Hamming84Codewords[reversed] >> (8 - codingRateDenominator));
    }

    private static int ReverseNibble(int value) =>
        ((value & 0x1) << 3) |
        ((value & 0x2) << 1) |
        ((value & 0x4) >> 1) |
        ((value & 0x8) >> 3);

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
