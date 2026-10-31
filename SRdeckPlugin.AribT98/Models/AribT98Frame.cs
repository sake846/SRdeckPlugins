namespace SRdeckPlugin.AribT98.Models;

public sealed record AribT98Frame(
    bool IsCommunication, int Mode, int MessageType, bool FirstUnit, int RemainingUnits,
    int? CallType, int? UserCode, int? Manufacturer, byte[] ControlBits,
    byte[] TrafficBits, byte[]? ParameterData, string? CallSign, int CorrectedBits)
{
    public AribT98Modulation Modulation { get; init; }
    public bool UsesArbitraryScrambler { get; init; }
    public bool TrafficDescrambled { get; init; } = true;
    // Relative soft confidence, in air-bit order after PN9 removal; null for
    // imported hard-only frames/PSK. Arrays are owned by this frame.
    public byte[]? TrafficReliability { get; init; }
    // PSK air TCH only (96 + 160 bits), before PN9 removal. Never treated as AMBE.
    public byte[]? RawTrafficBits { get; init; }
    public long SamplePosition { get; internal set; }
    public double FrequencyOffsetHz { get; internal set; }
    public double SyncError { get; internal set; }
    public bool Inverted { get; internal set; }
    public byte[]? Data1 { get; init; }
    public byte[]? Data2 { get; init; }
    public bool IsEnd => IsCommunication && Mode == 5 && (Modulation == AribT98Modulation.PiOver4Qpsk || MessageType == 30);
    public bool IsVoiceSlot(int slot) => Modulation == AribT98Modulation.FourFsk && IsCommunication && slot is >= 0 and < 4 &&
        (Mode == 3 || (Mode == 1 && slot >= 2) || (Mode == 2 && slot < 2));
    public string Kind => !IsCommunication ? "同期バースト" : Modulation == AribT98Modulation.PiOver4Qpsk ? Mode switch
    {
        1 => "音声 (PSK)", 2 => "データ (FECなし)", 3 => "データ (FECあり)",
        5 => "終話", 6 => "データ1 / 音声", 7 => "データ2 / 音声", _ => "未定義"
    } : Mode switch
    {
        0 => "データ (FECなし)", 1 => "データ / 音声", 2 => "音声 / データ",
        3 => "音声", 4 => "データ (FECあり)", 5 => "終話 / アイドル", _ => "予約"
    };
}
