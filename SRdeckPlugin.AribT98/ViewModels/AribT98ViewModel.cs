using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.AribT98.Models;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.AribT98.ViewModels;

public sealed record AribT98FrameRow(string Time, string Kind, string UserCode, string Privacy, string CallSign, string Corrections,
    string TrafficStatus, string TrafficTime, string RawTraffic, string Traffic1, string Traffic2, int FrameCount,
    DateTimeOffset ReceivedAtUtc)
{
    public string StationType { get; init; } = "—";
    public string Channel { get; init; } = "—";
}
public sealed record AribT98ModulationChoice(AribT98Modulation Value, string Label);

public sealed partial class AribT98ViewModel : ObservableObject
{
    // Frequency remains the persisted value, preserving existing and off-plan recordings.
    private AribT98Settings appliedSettings = new();
    private bool synchronizingChannel;
    private bool synchronizingHistoryLimit;
    private AribT98FrameRow? activeCall;
    private DateTimeOffset activeCallLastTime;
    private int activeFrameCount, activeCorrectionBits;
    private int? activeUserCode, activeCallType, activeManufacturer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Channels)), NotifyCanExecuteChangedFor(nameof(CallingChannelCommand))]
    private AribT98ChannelPlan selectedChannelPlan;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsManualFrequency)), NotifyPropertyChangedFor(nameof(IsChannelFrequency))]
    private AribT98Channel? selectedChannel = AribT98Channels.Calling;
    [ObservableProperty] private int selectedSettingsTabIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))]
    private long frequencyHz = 351_287_500;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings)), NotifyPropertyChangedFor(nameof(IsPsk)), NotifyPropertyChangedFor(nameof(IsFourFsk))]
    private AribT98Modulation modulation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int? pskScramblerSeed;
    [ObservableProperty] private string appliedChannelNumber = "15";
    [ObservableProperty] private string appliedChannelPrefix = "CH";
    [ObservableProperty] private string appliedChannel = "CH 15";
    [ObservableProperty] private string appliedChannelSummary = "種別：登録局　チャンネル：CH 15";
    [ObservableProperty] private string appliedChannelDescription = "呼出チャネル";
    [ObservableProperty] private string appliedUserCode = "ALL";
    [ObservableProperty] private string appliedPrivacy = "未設定";
    [ObservableProperty] private string appliedSpeaker = "ON / 100";
    [ObservableProperty] private string receivedPrivacy = "—";
    [ObservableProperty] private string appliedModulation = "STD-T98 4FSK";
    [ObservableProperty] private string pipeline = "48 kHz IQ → 周波数検波 → H(f)/sinc(fT) → 同期 → RICH / SACCH → AMBE+2";
    [ObservableProperty] private string protocolSummary = "第3編 / 2400 symbol/s / 80 ms / AMBE+2 / ドングル";
    [ObservableProperty] private AribT98FrameRow? selectedFrame;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int userCode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyKey;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyPreset1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyPreset2;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyPreset3;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyPreset4;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int privacyPreset5;
    [ObservableProperty] private int maximumHistory = 1_000;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private bool parallelPrivacySearchEnabled = true;
    [ObservableProperty] private string presetPrivacyStatus = "秘話探索：現在鍵 → プリセット0件 → 総当たり・秘話待ち";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private bool monitorAudioEnabled = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private string ambePort = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingSettings))] private int ambeBaudRate = 460800;
    [ObservableProperty] private string status = "停止中";
    [ObservableProperty] private string appliedFrequency = "351.287500 MHz";
    [ObservableProperty] private string settingsStatus = "フォーカス移動や選局ボタン操作で自動適用・保存します";
    [ObservableProperty] private string inputLevel = "—";
    [ObservableProperty] private string offset = "—";
    [ObservableProperty] private string syncQuality = "—";
    [ObservableProperty] private string lastUserCode = "—";
    [ObservableProperty] private string lastManufacturer = "—";
    [ObservableProperty] private string lastCallSign = "—";
    [ObservableProperty] private string lastMode = "—";
    [ObservableProperty] private string audioStatus = "音声待ち";
    [ObservableProperty] private float[] eyePattern = [];
    [ObservableProperty] private long acceptedFrames;
    [ObservableProperty] private long rejectedFrames;
    [ObservableProperty] private long audioFrames;
    [ObservableProperty] private string lastUpdated = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReceiving))] private OverallStatusKind overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private IPluginRuntimeDiagnostics runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    public ObservableCollection<AribT98FrameRow> Frames { get; } = [];
    public IReadOnlyList<AribT98Channel> Channels => AribT98Channels.ChoicesFor(SelectedChannelPlan);
    public IReadOnlyList<AribT98ChannelPlanChoice> ChannelPlans => AribT98Channels.Plans;
    public bool IsManualFrequency => SelectedChannel?.IsManual != false;
    public bool IsChannelFrequency => !IsManualFrequency;
    public bool IsPsk => Modulation == AribT98Modulation.PiOver4Qpsk;
    public bool IsFourFsk => !IsPsk;
    public int Volume => 100;
    public bool IsReceiving => OverallStatusKind == OverallStatusKind.Success;
    public bool HasPendingSettings => FrequencyHz != appliedSettings.FrequencyHz || Modulation != appliedSettings.Modulation ||
        PskScramblerSeed != appliedSettings.PskScramblerSeed || UserCode != appliedSettings.UserCode ||
        PrivacyKey != appliedSettings.PrivacyKey ||
        PrivacyPreset1 != appliedSettings.PrivacyKeyPresets[0] || PrivacyPreset2 != appliedSettings.PrivacyKeyPresets[1] ||
        PrivacyPreset3 != appliedSettings.PrivacyKeyPresets[2] || PrivacyPreset4 != appliedSettings.PrivacyKeyPresets[3] ||
        PrivacyPreset5 != appliedSettings.PrivacyKeyPresets[4] ||
        ParallelPrivacySearchEnabled != appliedSettings.ParallelPrivacySearchEnabled ||
        MonitorAudioEnabled != appliedSettings.MonitorAudioEnabled ||
        AmbePort != appliedSettings.AmbePort || AmbeBaudRate != appliedSettings.AmbeBaudRate;
    public IReadOnlyList<int> AmbeBaudRates { get; } = [460800, 230400];
    public IReadOnlyList<AribT98ModulationChoice> Modulations { get; } =
        [new(AribT98Modulation.FourFsk, "4FSK（第3編・AMBE+2音声）"), new(AribT98Modulation.PiOver4Qpsk, "π/4 QPSK（第1編・TCH抽出）")];
    public Func<AribT98Settings, Task>? ApplyRequested { get; set; }
    public Func<ValueTask>? ResetSettingsRequested { get; set; }
    public Action<int>? MaximumHistoryChanged { get; set; }
    public Action<AribT98FrameRow>? HistoryEntryCompleted { get; set; }
    public Action? ClearHistoryRequested { get; set; }

    partial void OnMaximumHistoryChanged(int value)
    {
        int normalized = Math.Clamp(value, 100, 100_000);
        if (value != normalized)
        {
            synchronizingHistoryLimit = true;
            MaximumHistory = normalized;
            synchronizingHistoryLimit = false;
        }
        TrimHistory();
        if (!synchronizingHistoryLimit) MaximumHistoryChanged?.Invoke(normalized);
    }

    partial void OnSelectedChannelChanged(AribT98Channel? value)
    {
        if (value is null) return;
        if (!value.IsManual && !synchronizingChannel) FrequencyHz = value.FrequencyHz;
    }

    partial void OnSelectedChannelPlanChanged(AribT98ChannelPlan value)
    {
        if (synchronizingChannel) return;
        var channels = AribT98Channels.ForPlan(value);
        int? previousNumber = SelectedChannel?.Number;
        // Replace the combo's items before selecting an item from the new plan.
        OnPropertyChanged(nameof(Channels));
        SelectedChannel = channels.FirstOrDefault(c => c.Number == previousNumber && !c.IsAirborne) ?? channels[0];
    }

    partial void OnFrequencyHzChanged(long value) => SynchronizeChannel(value);

    private void SynchronizeChannel(long frequencyHz)
    {
        var channel = AribT98Channels.Find(frequencyHz);
        synchronizingChannel = true;
        try
        {
            if (!channel.IsManual) SelectedChannelPlan = channel.Plan;
            SelectedChannel = channel;
        }
        finally { synchronizingChannel = false; }
    }

    [RelayCommand]
    private void NextChannel() => StepChannel(1);
    [RelayCommand]
    private void PreviousChannel() => StepChannel(-1);
    private bool CanSelectCallingChannel() => SelectedChannelPlan == AribT98ChannelPlan.Registered;
    [RelayCommand(CanExecute = nameof(CanSelectCallingChannel))]
    private void CallingChannel() { if (CanSelectCallingChannel()) SelectedChannel = AribT98Channels.Calling; }

    private void StepChannel(int step)
    {
        var channels = AribT98Channels.ForPlan(SelectedChannelPlan);
        int index = SelectedChannel is { IsManual: false } channel ? channels.ToList().IndexOf(channel) : -1;
        if (index < 0) index = step > 0 ? -1 : 0;
        SelectedChannel = channels[(index + step + channels.Count) % channels.Count];
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        try
        {
            if (ApplyRequested is null) return;
            await ApplyRequested(new() { FrequencyHz = FrequencyHz, UserCode = UserCode, PrivacyKey = PrivacyKey,
                PrivacyKeyPresets = [PrivacyPreset1, PrivacyPreset2, PrivacyPreset3, PrivacyPreset4, PrivacyPreset5],
                ParallelPrivacySearchEnabled = ParallelPrivacySearchEnabled,
                MaximumHistory = MaximumHistory,
                Modulation = Modulation, PskScramblerSeed = PskScramblerSeed,
                MonitorAudioEnabled = MonitorAudioEnabled,
                AmbePort = AmbePort.Trim(), AmbeBaudRate = AmbeBaudRate });
            SettingsStatus = "自動適用・保存しました";
        }
        catch (Exception e) { SettingsStatus = e.Message; }
    }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync() =>
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "デジタル簡易無線（STD-T98）",
            () => ResetSettingsRequested?.Invoke() ?? ValueTask.CompletedTask,
            () => { });

    [RelayCommand]
    private void ClearHistory()
    {
        Frames.Clear(); SelectedFrame = activeCall = null;
        activeFrameCount = activeCorrectionBits = 0;
        activeUserCode = activeCallType = activeManufacturer = null;
        ClearHistoryRequested?.Invoke();
    }

    public void LoadHistory(IEnumerable<AribT98FrameRow> rows)
    {
        Frames.Clear();
        foreach (AribT98FrameRow row in rows.Take(MaximumHistory)) Frames.Add(row);
        SelectedFrame = Frames.FirstOrDefault();
        activeCall = null;
    }

    public void CompleteActiveHistoryEntry()
    {
        if (activeCall is not null) HistoryEntryCompleted?.Invoke(activeCall);
        activeCall = null;
    }

    private void TrimHistory()
    {
        while (Frames.Count > MaximumHistory) Frames.RemoveAt(Frames.Count - 1);
        if (SelectedFrame is not null && !Frames.Contains(SelectedFrame)) SelectedFrame = Frames.FirstOrDefault();
    }

    public void Configure(AribT98Settings settings)
    {
        ResetPrivacyAnalysis();
        appliedSettings = settings;
        FrequencyHz = settings.FrequencyHz; UserCode = settings.UserCode; PrivacyKey = settings.PrivacyKey;
        PrivacyPreset1 = settings.PrivacyKeyPresets[0]; PrivacyPreset2 = settings.PrivacyKeyPresets[1];
        PrivacyPreset3 = settings.PrivacyKeyPresets[2]; PrivacyPreset4 = settings.PrivacyKeyPresets[3];
        PrivacyPreset5 = settings.PrivacyKeyPresets[4];
        ParallelPrivacySearchEnabled = settings.ParallelPrivacySearchEnabled;
        synchronizingHistoryLimit = true;
        MaximumHistory = settings.MaximumHistory;
        synchronizingHistoryLimit = false;
        var channel = AribT98Channels.Find(settings.FrequencyHz);
        SynchronizeChannel(settings.FrequencyHz);
        AppliedChannelNumber = channel.NumberText;
        AppliedChannelPrefix = channel.IsManual ? "MANUAL" : channel.Prefix;
        AppliedChannel = channel.Display;
        string channelSummary = channel.IsManual ? channel.Display : $"{channel.Prefix} {channel.NumberText}";
        AppliedChannelSummary = $"種別：{AribT98Channels.Plans.First(plan => plan.Value == SelectedChannelPlan).Label}　チャンネル：{channelSummary}";
        AppliedChannelDescription = channel.Description;
        AppliedUserCode = settings.UserCode == 0 ? "ALL" : settings.UserCode.ToString("D3");
        AppliedPrivacy = settings.PrivacyKey == 0 ? "未設定" : settings.PrivacyKey.ToString("D5");
        PresetPrivacyStatus = DefaultPresetPrivacyStatus(settings);
        AppliedSpeaker = settings.MonitorAudioEnabled ? $"ON / {Volume}" : "OFF";
        Modulation = settings.Modulation; PskScramblerSeed = settings.PskScramblerSeed;
        bool psk = settings.Modulation == AribT98Modulation.PiOver4Qpsk;
        if (psk) AppliedSpeaker = "PSK音声未対応";
        AppliedModulation = psk ? "STD-T98 π/4 QPSK" : "STD-T98 4FSK";
        Pipeline = psk ? "48 kHz IQ → RRC α=0.2 → 差動復調 → SW → RICH/PICH K=6・CRC → PN9解除 → TCH" :
            "48 kHz IQ → 周波数検波 → H(f)/sinc(fT) → 同期 → RICH / SACCH → AMBE+2";
        ProtocolSummary = psk ? "第1編 / 4800 symbol/s / 40 ms / CODEC・TCH音声配置は未規定（音声未対応）" :
            "第3編 / 2400 symbol/s / 80 ms / AMBE+2 / ドングル";
        AudioStatus = psk ? "PSK音声形式未対応・TCH抽出のみ" : "音声待ち";
        MonitorAudioEnabled = settings.MonitorAudioEnabled;
        AmbePort = settings.AmbePort; AmbeBaudRate = settings.AmbeBaudRate;
        AppliedFrequency = $"{settings.FrequencyHz / 1_000_000.0:F6} MHz";
        OnPropertyChanged(nameof(HasPendingSettings));
    }

    public void ClearReception(bool preserveHistory = false)
    {
        if (preserveHistory) CompleteActiveHistoryEntry();
        else activeCall = null;
        ResetPrivacyAnalysis();
        PresentPresetPrivacy(0, DefaultPresetPrivacyStatus(appliedSettings));
        OverallStatusKind = OverallStatusKind.Idle;
        LastUserCode = LastManufacturer = LastMode = "—";
        ReceivedPrivacy = "—";
        Offset = SyncQuality = InputLevel = LastUpdated = "—";
        AudioStatus = Modulation == AribT98Modulation.PiOver4Qpsk ? "PSK音声形式未対応・TCH抽出のみ" : "音声待ち";
        EyePattern = [];
        AcceptedFrames = RejectedFrames = AudioFrames = 0;
        activeFrameCount = activeCorrectionBits = 0;
        activeUserCode = activeCallType = activeManufacturer = null;
        if (!preserveHistory)
        {
            Frames.Clear();
            SelectedFrame = null;
        }
    }

    public void PresentPresetPrivacy(int key, string message)
    {
        AppliedPrivacy = key > 0 ? key.ToString("D5") : appliedSettings.PrivacyKey == 0 ? "未設定" : appliedSettings.PrivacyKey.ToString("D5");
        PresetPrivacyStatus = appliedSettings.Modulation == AribT98Modulation.FourFsk ? message : "秘話探索：PSKは対象外";
    }

    public void AdoptPrivacyKey(int key, bool saved = true)
    {
        if (key is < 1 or > 32767) throw new ArgumentOutOfRangeException(nameof(key));
        ResetPrivacyAnalysis();
        appliedSettings = appliedSettings with { PrivacyKey = key };
        PrivacyKey = key;
        AppliedPrivacy = key.ToString("D5");
        SettingsStatus = saved ? $"現在の秘話鍵を {key:D5} に自動設定・保存しました" :
            $"現在の秘話鍵を {key:D5} に自動設定しました（保存に失敗）";
        OnPropertyChanged(nameof(HasPendingSettings));
    }

    private static string DefaultPresetPrivacyStatus(AribT98Settings settings)
    {
        if (settings.Modulation != AribT98Modulation.FourFsk) return "秘話探索：PSKは対象外";
        int count = settings.PrivacyKeyPresets.Count(key => key != 0);
        return $"秘話探索：現在鍵 → プリセット{count}件 → 総当たり・秘話待ち";
    }

    public void Observe(AribT98Frame frame, DateTimeOffset time)
    {
        CapturePrivacyFrame(frame);
        if (frame.UserCode is int uc) LastUserCode = uc.ToString("D3");
        LastManufacturer = frame.Manufacturer?.ToString() ?? "—";
        // Keep the last decoded call sign visible until another one is received.
        if (!string.IsNullOrWhiteSpace(frame.CallSign)) LastCallSign = frame.CallSign;
        LastMode = frame.Kind;
        LastUpdated = time.ToLocalTime().ToString("HH:mm:ss.fff");
        bool psk = frame.Modulation == AribT98Modulation.PiOver4Qpsk;
        ReceivedPrivacy = psk ? "PSK / TCH" : frame.CallType switch { 0 => "通常", 1 => "秘話", _ => "—" };
        bool identityChanged = activeCall is not null && frame.FirstUnit &&
            (frame.UserCode is int incomingUc && activeUserCode is int previousUc && incomingUc != previousUc ||
             frame.CallType is int incomingCallType && activeCallType is int previousCallType && incomingCallType != previousCallType ||
             frame.Manufacturer is int incomingManufacturer && activeManufacturer is int previousManufacturer && incomingManufacturer != previousManufacturer);
        if (activeCall is not null && (time - activeCallLastTime > TimeSpan.FromMilliseconds(500) || identityChanged))
        {
            HistoryEntryCompleted?.Invoke(activeCall);
            activeCall = null;
        }
        bool followLatest = SelectedFrame is null || ReferenceEquals(SelectedFrame, activeCall) ||
            Frames.Count > 0 && ReferenceEquals(SelectedFrame, Frames[0]);
        AribT98FrameRow? previous = activeCall;
        if (previous is null)
        {
            activeFrameCount = activeCorrectionBits = 0;
            activeUserCode = activeCallType = activeManufacturer = null;
        }
        activeFrameCount++;
        activeCorrectionBits += frame.CorrectedBits;
        activeUserCode ??= frame.UserCode;
        activeCallType ??= frame.CallType;
        activeManufacturer ??= frame.Manufacturer;
        int split = psk ? 96 : 144;
        string trafficStatus = !frame.IsCommunication ? "同期バースト：TCHなし" : psk ?
            !frame.TrafficDescrambled ? "S=1：任意コード未設定。受信TCHのみ（未解除）" :
            frame.UsesArbitraryScrambler ? "S=1：指定コードでPN9解除（コードの正否・TCH CRCは検証不可）" :
            "S=0：UCでPN9解除。TCHの誤り訂正・CRC・音声形式は未解釈" : "第3編：ホワイトニング解除後のTCH（音声FEC前）";
        bool hasTraffic = frame.IsCommunication && !frame.IsEnd;
        DateTimeOffset callStartedAt = previous?.ReceivedAtUtc ?? time;
        string callStart = previous?.Time ?? callStartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        string stationType = AribT98Channels.Plans.First(plan => plan.Value == SelectedChannelPlan).Label;
        AribT98Channel appliedChannel = AribT98Channels.Find(appliedSettings.FrequencyHz);
        string channel = appliedChannel.IsManual ? "—" : $"{appliedChannel.Prefix} {appliedChannel.NumberText}";
        string kind = frame.IsEnd ? previous?.Kind ?? frame.Kind : frame.Kind;
        var row = new AribT98FrameRow(callStart, kind,
            frame.UserCode?.ToString("D3") ?? previous?.UserCode ?? "—",
            psk ? (frame.UsesArbitraryScrambler ? "S=1" : "S=0") : frame.CallType switch
                { 0 => "通常", 1 => "秘話", _ => previous?.Privacy ?? "—" },
            frame.CallSign ?? previous?.CallSign ?? "—", activeCorrectionBits.ToString(),
            hasTraffic ? trafficStatus : previous?.TrafficStatus ?? trafficStatus,
            hasTraffic ? LastUpdated : previous?.TrafficTime ?? "—",
            hasTraffic ? Hex(frame.RawTrafficBits) : previous?.RawTraffic ?? "—",
            hasTraffic && frame.TrafficBits.Length >= split ? Hex(frame.TrafficBits[..split]) : previous?.Traffic1 ?? "—",
            hasTraffic && frame.TrafficBits.Length > split ? Hex(frame.TrafficBits[split..]) : previous?.Traffic2 ?? "—",
            activeFrameCount, callStartedAt.ToUniversalTime())
        {
            StationType = previous?.StationType ?? stationType,
            Channel = previous?.Channel ?? channel
        };
        if (previous is null) Frames.Insert(0, row);
        else Frames[0] = row;
        activeCall = frame.IsEnd ? null : row;
        if (frame.IsEnd) HistoryEntryCompleted?.Invoke(row);
        activeCallLastTime = time;
        if (followLatest) SelectedFrame = row;
        TrimHistory();
    }

    private static string Hex(byte[]? bits)
    {
        if (bits is null || bits.Length == 0) return "—";
        byte[] bytes = new byte[(bits.Length+7)/8];
        for (int i = 0; i < bits.Length; i++) bytes[i/8] |= (byte)(bits[i] << (7-i%8));
        return Convert.ToHexString(bytes);
    }
}
