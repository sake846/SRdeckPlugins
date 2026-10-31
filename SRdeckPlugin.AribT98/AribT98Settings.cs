using SRdeck.Codecs.Ambe;
namespace SRdeckPlugin.AribT98;

public enum AribT98Modulation { FourFsk, PiOver4Qpsk }

public sealed record AribT98Settings
{
    // STD-T98 v1.1 Part 3, registration channel 15. Manual tuning also supports recordings.
    public long FrequencyHz { get; init; } = 351_287_500;
    public AribT98Modulation Modulation { get; init; }
    // Part 1 RICH S=1: known arbitrary PN9 seed; null means unknown, zero is valid.
    public int? PskScramblerSeed { get; init; }
    public int UserCode { get; init; }
    public int PrivacyKey { get; init; }
    public int[] PrivacyKeyPresets { get; init; } = new int[5];
    public bool ParallelPrivacySearchEnabled { get; init; } = true;
    public int MaximumHistory { get; init; } = 1_000;
    public bool MonitorAudioEnabled { get; init; } = true;
    public string AmbePort { get; init; } = "";
    public int AmbeBaudRate { get; init; } = 460800;

    public void Validate()
    {
        if (!Enum.IsDefined(Modulation)) throw new ArgumentOutOfRangeException(nameof(Modulation));
        if (PskScramblerSeed is < 0 or > 511) throw new ArgumentOutOfRangeException(nameof(PskScramblerSeed), "PSK任意コードは空欄（未設定）または0～511で指定してください。");
        if (FrequencyHz is < 1_000_000 or > 6_000_000_000) throw new ArgumentOutOfRangeException(nameof(FrequencyHz), "周波数は1 MHz～6 GHzで指定してください。");
        if (UserCode is < 0 or > 511) throw new ArgumentOutOfRangeException(nameof(UserCode), "ユーザコードは0～511で指定してください。");
        if (PrivacyKey is < 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(PrivacyKey), "秘話鍵は0（未設定）または1～32767で指定してください。");
        if (PrivacyKeyPresets is not { Length: 5 } || PrivacyKeyPresets.Any(key => key is < 0 or > 32767))
            throw new ArgumentOutOfRangeException(nameof(PrivacyKeyPresets), "秘話コードのプリセットは5件、各0（未設定）または1～32767で指定してください。");
        if (PrivacyKeyPresets.Where(key => key != 0).Distinct().Count() != PrivacyKeyPresets.Count(key => key != 0))
            throw new ArgumentException("秘話コードのプリセットに同じコードを重複して指定できません。", nameof(PrivacyKeyPresets));
        if (MaximumHistory is < 100 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumHistory), "履歴保持件数は100～100,000件で指定してください。");
        AmbeDongleDecoder.ValidateSettings(AmbePort, AmbeBaudRate);
    }
}
