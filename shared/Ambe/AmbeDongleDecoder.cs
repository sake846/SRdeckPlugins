using System.Buffers.Binary;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace SRdeck.Codecs.Ambe;

/// <summary>AMBE-3000 packet mode over a COM port or AMBEserver UDP endpoint.</summary>
internal sealed class AmbeDongleDecoder : IDisposable
{
    private readonly SerialPort? port;
    private readonly Socket? server;
    private readonly byte[] queuedPackets = new byte[5 * 15];
    private bool parityEnabled;
    private bool needsReset;

    public AmbeDongleDecoder(string portName, int baudRate, bool dstar = false)
    {
        ValidateSettings(portName, baudRate);
        try
        {
            if (portName.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = new Uri(portName);
                // AMBEserver commonly listens on IPv4; UDP connect cannot detect an IPv6-only DNS mismatch.
                var addresses = Dns.GetHostAddresses(endpoint.DnsSafeHost);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ??
                    addresses.FirstOrDefault() ?? throw new IOException("AMBEサーバーのホスト名を解決できません。");
                server = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 500, SendTimeout = 500 };
                server.Connect(address, endpoint.Port < 0 ? 2460 : endpoint.Port);
            }
            else
            {
                port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = 100,
                    WriteTimeout = 100
                };
                port.Open();
                port.DiscardInBuffer();
            }
            // ThumbDV firmware is seen with either DVSI packet parity setting.
            try { Probe(parity: false); }
            catch (TimeoutException)
            {
                port?.DiscardInBuffer();
                Probe(parity: true);
            }
            if (dstar)
                Configure(0x0A, [0x01, 0x30, 0x07, 0x63, 0x40, 0, 0, 0, 0, 0, 0, 0x48]); // AMBE 2400 + 1200 FEC.
            else Control(0x09, 34); // 2450 bit/s speech, no FEC: the host already corrected the 72-bit frame.
            Control(0x32, 0);  // 16-bit linear PCM, not A-law or mu-law.
            Control(0x0B, 2);  // Initialize the decoder.
        }
        catch (SocketException e) { Dispose(); throw new IOException($"AMBEサーバーに接続できません: {e.Message}", e); }
        catch { Dispose(); throw; }
    }

    public static string DeviceName(string? endpoint) =>
        endpoint?.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) == true ? "AMBEサーバー" : "DVStick";

    public void Reset() => needsReset = true;

    public void SynthesizeDstar(ReadOnlySpan<byte> ambe, Span<short> pcm)
    {
        if (pcm.Length < 160) throw new ArgumentException("Expected 160 PCM samples.", nameof(pcm));
        byte[] packet = BuildDstarChannelPacket(ambe, parityEnabled);
        if (needsReset) { Control(0x0B, 2); needsReset = false; }
        WritePackets(packet, packet.Length);
        DecodeSpeech(ReadPacket(2), pcm);
    }

    internal static byte[] BuildDstarChannelPacket(ReadOnlySpan<byte> ambe, bool parity = true)
    {
        if (ambe.Length != 9) throw new ArgumentException("Expected nine packed D-STAR AMBE bytes.", nameof(ambe));
        return BuildPacket(1, [0x01, 72, .. ambe], parity);
    }

    public void Synthesize(ReadOnlySpan<byte> parameters, byte voiceMask, Span<short> pcm)
    {
        if (pcm.Length < parameters.Length / 49 * 160)
            throw new ArgumentException("Expected 160 PCM samples per AMBE slot.");
        int queued = BuildChannelPackets(parameters, voiceMask, queuedPackets, parityEnabled);
        int slots = parameters.Length / 49;
        if (needsReset) { Control(0x0B, 2); needsReset = false; }
        // Queue the whole frame before reading responses; one-at-a-time USB round trips
        // cannot reliably keep up with 20 ms AMBE slots.
        WritePackets(queuedPackets, queued);
        for (int slot = 0; slot < slots; slot++)
        {
            if ((voiceMask & (1 << slot)) == 0) continue;
            byte[] body = ReadPacket(2);
            DecodeSpeech(body, pcm.Slice(slot * 160, 160));
        }
    }

    internal static int BuildChannelPackets(ReadOnlySpan<byte> parameters, byte voiceMask, Span<byte> packets, bool parity)
    {
        int slots = parameters.Length / 49;
        if (slots is < 1 or > 5 || parameters.Length != slots * 49 || (voiceMask >> slots) != 0 || packets.Length < slots * 15)
            throw new ArgumentException("Expected one to five AMBE slots and space for their channel packets.");
        int queued = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            if ((voiceMask & (1 << slot)) == 0) continue;
            byte[] packet = BuildChannelPacket(parameters.Slice(slot * 49, 49), parity);
            packet.CopyTo(packets[queued..]);
            queued += packet.Length;
        }
        return queued;
    }

    public static void ValidateSettings(string portName, int baudRate)
    {
        if (portName is not null && portName.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(portName, UriKind.Absolute, out var endpoint) ||
                Uri.CheckHostName(endpoint.DnsSafeHost) == UriHostNameType.Unknown || endpoint.Port is 0 ||
                endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath is not ("" or "/") ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("AMBEサーバーは udp://ホスト名:ポート番号 で指定してください（例: udp://192.168.1.10:2460）。", nameof(portName));
        }
        else if (portName is null || (portName.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(portName, @"^COM[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new ArgumentException("AMBE接続先は COM 番号または udp://ホスト名:ポート番号 で指定してください（例: COM6 / udp://192.168.1.10:2460）。", nameof(portName));
        if (baudRate is not (460800 or 230400))
            throw new ArgumentOutOfRangeException(nameof(baudRate), "AMBEドングルの通信速度は 460800 または 230400 にしてください。");
    }

    internal static byte[] BuildChannelPacket(ReadOnlySpan<byte> parameters, bool parity = true)
    {
        if (parameters.Length != 49 || parameters.IndexOfAnyExcept((byte)0, (byte)1) >= 0)
            throw new ArgumentException("Expected 49 binary AMBE bits.");
        Span<byte> body = stackalloc byte[9];
        body[0] = 0x01; // CHAND
        body[1] = 49;
        for (int i = 0; i < 49; i++) body[2 + i / 8] |= (byte)(parameters[i] << (7 - i % 8));
        return BuildPacket(1, body, parity);
    }

    private void Control(byte field, byte value) => Configure(field, [value]);

    private void Configure(byte field, ReadOnlySpan<byte> values)
    {
        byte[] packet = BuildPacket(0, [field, .. values], parityEnabled);
        WritePackets(packet, packet.Length);
        byte[] response = ReadPacket(0);
        ValidateControlResponse(field, response);
    }

    internal static void ValidateControlResponse(byte field, ReadOnlySpan<byte> response)
    {
        if (response.Length == 2 && response[0] == field && response[1] == 0) return;
        string detail = $"0x{field:X2}, 応答: {Convert.ToHexString(response)}";
        if (field == 0x0A && response.Length == 2 && response[0] == field)
            throw new IOException($"ドングルがD-STAR音声モード (2400 + 1200 bps) を拒否しました ({detail})。D-STAR対応を確認してください。");
        throw new IOException($"AMBE dongle rejected control field ({detail}).");
    }

    private void Probe(bool parity)
    {
        byte[] packet = BuildPacket(0, [0x30], parity); // Product ID, no configuration change.
        WritePackets(packet, packet.Length);
        for (int i = 0; i < 2; i++)
        {
            byte[] response = ReadPacket(0, parity);
            if (response.Length > 0 && response[0] == 0x39) continue; // Late READY after USB open.
            if (response.Length >= 2 && response[0] == 0x30) { parityEnabled = parity; return; }
            break;
        }
        throw new IOException("AMBE dongle did not identify itself.");
    }

    private static byte[] BuildPacket(byte type, ReadOnlySpan<byte> body, bool parity = true)
    {
        byte[] packet = new byte[body.Length + (parity ? 6 : 4)];
        packet[0] = 0x61;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)(body.Length + (parity ? 2 : 0)));
        packet[3] = type;
        body.CopyTo(packet.AsSpan(4));
        if (parity)
        {
            packet[^2] = 0x2F;
            for (int i = 1; i < packet.Length - 1; i++) packet[^1] ^= packet[i];
        }
        return packet;
    }

    private byte[] ReadPacket(byte expectedType, bool? parity = null)
    {
        bool hasParity = parity ?? parityEnabled;
        byte[] header = new byte[4];
        byte[]? datagram = null;
        if (server is null) ReadExactly(header);
        else
        {
            datagram = new byte[516];
            int count;
            try { count = server.Receive(datagram); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            { throw new TimeoutException("AMBEサーバーからの応答がありません。", e); }
            catch (SocketException e) { throw new IOException($"AMBEサーバー受信失敗: {e.Message}", e); }
            if (count < 4) throw new IOException("AMBEサーバーの応答が短すぎます。");
            datagram = datagram[..count];
            datagram.AsSpan(0, 4).CopyTo(header);
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (header[0] != 0x61 || header[3] != expectedType || length < (hasParity ? 2 : 1) || length > 512)
            throw new IOException("Unexpected AMBE packet header.");
        if (datagram is not null && datagram.Length != length + 4)
            throw new IOException("AMBEサーバーの応答長が一致しません。");
        byte[] payload = new byte[length];
        if (datagram is null) ReadExactly(payload);
        else datagram.AsSpan(4).CopyTo(payload);
        if (!hasParity) return payload;
        byte checksum = 0;
        foreach (byte b in header.AsSpan(1)) checksum ^= b;
        foreach (byte b in payload[..^1]) checksum ^= b;
        if (payload[^2] != 0x2F || payload[^1] != checksum)
            throw new IOException("AMBE dongle packet parity failed.");
        return payload[..^2];
    }

    private void WritePackets(byte[] packets, int count)
    {
        if (server is null) { port!.Write(packets, 0, count); return; }
        try
        {
            // AMBEserver requires exactly one DVSI packet in each UDP datagram.
            for (int offset = 0; offset < count;)
            {
                int length = 4 + BinaryPrimitives.ReadUInt16BigEndian(packets.AsSpan(offset + 1));
                if (server.Send(packets.AsSpan(offset, length)) != length)
                    throw new IOException("AMBEサーバーへパケット全体を送信できませんでした。");
                offset += length;
            }
        }
        catch (SocketException e) { throw new IOException($"AMBEサーバー送信失敗: {e.Message}", e); }
    }

    private void ReadExactly(byte[] buffer)
    {
        for (int read = 0; read < buffer.Length;)
        {
            int count = port!.Read(buffer, read, buffer.Length - read);
            if (count == 0) throw new EndOfStreamException("AMBE dongle disconnected.");
            read += count;
        }
    }

    internal static void DecodeSpeech(ReadOnlySpan<byte> body, Span<short> pcm)
    {
        if (pcm.Length < 160) throw new ArgumentException("Expected 160 PCM samples.");
        if (!body.IsEmpty && body[0] == 0x40) body = body[1..];
        if (body.Length != 322 || body[0] != 0x00 || body[1] != 160)
            throw new IOException("AMBE dongle did not return 160 linear PCM samples.");
        for (int i = 0; i < 160; i++)
            pcm[i] = BinaryPrimitives.ReadInt16BigEndian(body.Slice(2 + i * 2, 2));
    }

    public void Dispose() { port?.Dispose(); server?.Dispose(); }
}
