using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SRdeckPlugin.HfFax.Models;

namespace SRdeckPlugin.HfFax.Storage;

internal static class HfFaxPngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Save(string path, HfFaxImageSnapshot image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 ||
            image.Pixels.Length != checked(image.Width * image.Height))
            throw new ArgumentException("The HF-FAX image dimensions are invalid.", nameof(image));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(Signature);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), image.Height);
        header[8] = 8;
        header[9] = 0;
        WriteChunk(stream, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] row = new byte[image.Width + 1];
            for (int y = 0; y < image.Height; y++)
            {
                row[0] = 0;
                image.Pixels.AsSpan(y * image.Width, image.Width).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = ComputeCrc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }
}
