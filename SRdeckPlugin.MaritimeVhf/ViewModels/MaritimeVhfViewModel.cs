using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.MaritimeVhf.Models;
using SRdeckPlugin.MaritimeVhf.Protocols;

namespace SRdeckPlugin.MaritimeVhf.ViewModels;

public sealed partial class MaritimeVhfViewModel : ObservableObject
{
    private bool synchronizing;
    [ObservableProperty] private MaritimeVhfChannel _selectedChannel = MaritimeVhfChannelPlan.Channel16;
    [ObservableProperty] private MaritimeVhfReceiveSideOption _selectedReceiveSide;
    [ObservableProperty] private bool _monitorAudioEnabled = true;
    [ObservableProperty] private int _monitorAudioVolume = 80;
    [ObservableProperty] private bool _squelchEnabled = true;
    [ObservableProperty] private float _squelchThresholdDbm = -110f;
    [ObservableProperty] private bool _dscWatchEnabled = true;
    [ObservableProperty] private bool _autoFollowEnabled = true;
    [ObservableProperty] private string _ownMmsi = string.Empty;
    [ObservableProperty] private int _autoReturnSeconds = 60;
    [ObservableProperty] private string _status = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _activeChannelText = "CH 16 / 156.800 MHz";
    [ObservableProperty] private string _dscStatusText = "CH 70 待機中";
    [ObservableProperty] private string _signalLevelText = "—";
    [ObservableProperty] private string _squelchStatusText = "閉";
    [ObservableProperty] private string _followStatusText = "自動追尾: 待機";
    [ObservableProperty] private string _tuningStatusText = "選局待機中";
    [ObservableProperty] private long _validDscMessages;
    [ObservableProperty] private DscMessageRow? _selectedMessage;
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;

    public MaritimeVhfViewModel()
    {
        ReceiveSides =
        [
            new(MaritimeVhfReceiveSide.Ship, "船舶送信側"),
            new(MaritimeVhfReceiveSide.Coast, "海岸局送信側")
        ];
        SelectedReceiveSide = ReceiveSides[0];
    }

    public IReadOnlyList<MaritimeVhfChannel> Channels => MaritimeVhfChannelPlan.Channels;
    public IReadOnlyList<MaritimeVhfReceiveSideOption> ReceiveSides { get; }
    public ObservableCollection<DscMessageRow> Messages { get; } = [];
    public string SettingStatus =>
        $"CH {SelectedChannel.Number} / {SelectedReceiveSide.DisplayName} / " +
        $"DSC {(DscWatchEnabled ? "ON" : "OFF")} / 追尾 {(AutoFollowEnabled ? "ON" : "OFF")}";

    internal Action<MaritimeVhfSettings>? SettingsChanged { get; set; }
    internal Func<Task>? ReturnFromFollowRequested { get; set; }
    internal Func<Task>? ResetPluginSettingsRequested { get; set; }
    internal Action? ClearRequested { get; set; }

    partial void OnSelectedChannelChanged(MaritimeVhfChannel value) => SettingsUpdated();
    partial void OnSelectedReceiveSideChanged(MaritimeVhfReceiveSideOption value) => SettingsUpdated();
    partial void OnMonitorAudioEnabledChanged(bool value) => SettingsUpdated();
    partial void OnMonitorAudioVolumeChanged(int value)
    {
        if (value != Math.Clamp(value, 0, 100)) MonitorAudioVolume = Math.Clamp(value, 0, 100);
        else SettingsUpdated();
    }
    partial void OnSquelchEnabledChanged(bool value) => SettingsUpdated();
    partial void OnSquelchThresholdDbmChanged(float value)
    {
        if (Math.Abs(value - Math.Clamp(value, -160f, -20f)) > 0.01f)
            SquelchThresholdDbm = Math.Clamp(value, -160f, -20f);
        else SettingsUpdated();
    }
    partial void OnDscWatchEnabledChanged(bool value) => SettingsUpdated();
    partial void OnAutoFollowEnabledChanged(bool value) => SettingsUpdated();
    partial void OnOwnMmsiChanged(string value) => SettingsUpdated();
    partial void OnAutoReturnSecondsChanged(int value)
    {
        if (value != Math.Clamp(value, 0, 600)) AutoReturnSeconds = Math.Clamp(value, 0, 600);
        else SettingsUpdated();
    }

    internal void SynchronizeSettings(MaritimeVhfSettings value)
    {
        synchronizing = true;
        try
        {
            SelectedChannel = MaritimeVhfChannelPlan.Find(value.SelectedChannel) ??
                MaritimeVhfChannelPlan.Channel16;
            SelectedReceiveSide = ReceiveSides.First(side => side.Value == value.ReceiveSide);
            MonitorAudioEnabled = value.MonitorAudioEnabled;
            MonitorAudioVolume = value.MonitorAudioVolume;
            SquelchEnabled = value.SquelchEnabled;
            SquelchThresholdDbm = value.SquelchThresholdDbm;
            DscWatchEnabled = value.DscWatchEnabled;
            AutoFollowEnabled = value.AutoFollowEnabled;
            OwnMmsi = value.OwnMmsi;
            AutoReturnSeconds = value.AutoReturnSeconds;
            OnPropertyChanged(nameof(SettingStatus));
        }
        finally
        {
            synchronizing = false;
        }
    }

    internal void AddMessage(DscMessage message, int maximum)
    {
        var row = new DscMessageRow(message.ReceivedAt.ToLocalTime(),
            DscProtocolParser.PriorityText(message.Priority),
            message.SourceMmsi,
            string.IsNullOrWhiteSpace(message.DestinationMmsi) ? "全局" : message.DestinationMmsi,
            message.WorkingChannel?.DisplayName ?? "—",
            message.Summary);
        Messages.Insert(0, row);
        while (Messages.Count > maximum) Messages.RemoveAt(Messages.Count - 1);
        SelectedMessage ??= row;
        ValidDscMessages++;
    }

    internal void UpdateReceiver(
        string activeChannel,
        string dscStatus,
        float signalDbm,
        bool squelchOpen,
        string tuningStatus,
        bool following,
        string followStatus)
    {
        ActiveChannelText = activeChannel;
        DscStatusText = dscStatus;
        SignalLevelText = float.IsFinite(signalDbm) ? $"{signalDbm:F1} dBm" : "—";
        SquelchStatusText = squelchOpen ? "開" : "閉";
        TuningStatusText = tuningStatus;
        FollowStatusText = followStatus;
        Status = following ? "DSC 自動追尾中" : squelchOpen ? "音声受信中" : "受信中";
        OverallStatusKind = following || squelchOpen
            ? OverallStatusKind.Success
            : OverallStatusKind.Running;
    }

    [RelayCommand]
    private async Task ReturnFromFollowAsync()
    {
        if (ReturnFromFollowRequested is not null) await ReturnFromFollowRequested();
    }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        if (ResetPluginSettingsRequested is not null) await ResetPluginSettingsRequested();
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        SelectedMessage = null;
        ClearRequested?.Invoke();
    }

    private void SettingsUpdated()
    {
        OnPropertyChanged(nameof(SettingStatus));
        if (synchronizing || SelectedChannel is null || SelectedReceiveSide is null) return;
        SettingsChanged?.Invoke(new MaritimeVhfSettings(
            SelectedChannel.Number,
            SelectedReceiveSide.Value,
            MonitorAudioEnabled,
            MonitorAudioVolume,
            SquelchEnabled,
            SquelchThresholdDbm,
            DscWatchEnabled,
            AutoFollowEnabled,
            OwnMmsi,
            AutoReturnSeconds));
    }
}
