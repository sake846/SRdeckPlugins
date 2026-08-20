using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

/// <summary>
/// Decrypts packets sent on Meshtastic's factory-default LongFast channel.
/// Meshtastic uses AES-CTR with packet id and sender id in the nonce.
/// </summary>
public static class MeshtasticChannelDecryptor
{
    public static readonly byte[] DefaultLongFastKey =
    [
        0xD4, 0xF1, 0xBB, 0x3A, 0x20, 0x29, 0x07, 0x59,
        0xF0, 0xBC, 0xFF, 0xAB, 0xCF, 0x4E, 0x69, 0x01
    ];

    public static byte DefaultLongFastChannelHash { get; } = CalculateChannelHash("LongFast", DefaultLongFastKey);
    public static bool TryDecryptDefaultLongFast(MeshtasticRadioPacket packet, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return TryDecrypt(packet, "LongFast", DefaultLongFastKey, out plaintext);
    }

    public static bool TryDecrypt(MeshtasticRadioPacket packet, string channelName, ReadOnlySpan<byte> key, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.ChannelHash != CalculateChannelHash(channelName, key) || packet.EncryptedPayload.Length == 0)
        {
            plaintext = [];
            return false;
        }

        Span<byte> counter = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(counter, packet.PacketId);
        BinaryPrimitives.WriteUInt32LittleEndian(counter[8..], packet.From);

        plaintext = new byte[packet.EncryptedPayload.Length];
        byte[] counterBlock = counter.ToArray();
        byte[] keyStream = new byte[16];

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        for (int offset = 0; offset < plaintext.Length; offset += keyStream.Length)
        {
            encryptor.TransformBlock(counterBlock, 0, counterBlock.Length, keyStream, 0);
            int count = Math.Min(keyStream.Length, plaintext.Length - offset);
            for (int index = 0; index < count; index++)
            {
                plaintext[offset + index] = (byte)(packet.EncryptedPayload[offset + index] ^ keyStream[index]);
            }

            IncrementCounter(counterBlock);
        }

        return true;
    }

    public static bool TryDecrypt(MeshtasticRadioPacket packet, string channelName, byte[] key, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryDecrypt(packet, channelName, key.AsSpan(), out plaintext);
    }

    public static byte CalculateChannelHash(string channelName, ReadOnlySpan<byte> key)
    {
        byte hash = 0;
        foreach (byte value in Encoding.UTF8.GetBytes(channelName)) hash ^= value;
        foreach (byte value in key) hash ^= value;
        return hash;
    }

    // AES-CTR increments the right-most 32-bit block counter in network order.
    private static void IncrementCounter(Span<byte> counter)
    {
        for (int index = counter.Length - 1; index >= 12; index--)
        {
            if (++counter[index] != 0) break;
        }
    }
}
