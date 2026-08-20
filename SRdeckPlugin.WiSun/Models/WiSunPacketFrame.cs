using System;

namespace SRdeckPlugin.WiSun.Models;

public sealed record WiSunPacketFrame
{
    public required DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
    public required long FrequencyHz { get; init; }
    public required double DurationMs { get; init; }
    public required float PeakDbfs { get; init; }
    public float PeakDbm { get; init; }
    public required float SnrDb { get; init; }
    public required byte[] RawPayload { get; init; }

    public string FrameType { get; init; } = "IEEE 802.15.4g SUN";
    public byte? SequenceNumber { get; init; }
    public ushort? PanId { get; init; }
    public string? SrcAddress { get; init; }
    public string? DstAddress { get; init; }
    public int FrameLengthBytes { get; init; }
    public bool? CrcValid { get; init; }
    public string ProtocolSummary { get; init; } = "Wi-SUN / 802.15.4g Frame";

    public string PanIdText => PanId is ushort panId ? $"PAN ID {panId:X4}" : "PAN ID 不明";
    public string SourceNodeText => string.IsNullOrWhiteSpace(SrcAddress) ? "送信元不明" : SrcAddress;
    public string DestinationNodeText => string.IsNullOrWhiteSpace(DstAddress) ? "宛先不明" : DstAddress;
    public string CommunicationText => $"{SourceNodeText} → {DestinationNodeText}";
    public string LinkQualityText =>
        $"S/N {SnrDb:F1} dB / CRC {CrcValid switch { true => "OK", false => "NG", null => "—" }}";

    public string RawHexString => Convert.ToHexString(RawPayload);

    public string AsciiString
    {
        get
        {
            char[] chars = new char[RawPayload.Length];
            for (int i = 0; i < RawPayload.Length; i++)
            {
                byte b = RawPayload[i];
                chars[i] = (b >= 32 && b <= 126) ? (char)b : '.';
            }
            return new string(chars);
        }
    }

    public string SummaryText =>
        $"[Wi-SUN / 802.15.4g] {FrequencyHz / 1e6:F3} MHz | Type: {FrameType} | Seq: {SequenceNumber?.ToString() ?? "--"} | PAN: {PanId?.ToString("X4") ?? "--"} | {DurationMs:F1} ms | SNR: {SnrDb:F1} dB";
}
