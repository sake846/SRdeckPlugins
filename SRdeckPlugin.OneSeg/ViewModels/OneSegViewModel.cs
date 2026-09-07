using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Dsp;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.ViewModels;

internal sealed partial class OneSegViewModel : ObservableObject
{
    private OneSegPrefecture selectedPrefecture = OneSegPrefecture.Find("tokyo");
    private OneSegChannel selectedChannel = OneSegPrefecture.Find("tokyo").CandidateChannels.Last();
    private string status = "未初期化";
    private string ofdmLockStatus = "未同期";
    private string tmccLockStatus = "未同期";
    private string merStatus = "—";
    private string rsQualityStatus = "—";
    private string lockStatus = "OFDM -- / TMCC --";
    private string signalStatus = "MER -- dB / CFO -- Hz";
    private string packetStatus = "TS 0 / RS棄却 0 / CC誤り 0";
    private string caption = "";
    private string recordingStatus = "録画停止";
    private string captureStatus = "IQ保存: 受信待ち";
    private bool recording;
    private int volume = 100;
    private MediaPlayer? mediaPlayer;
    private OneSegStation? selectedStation;
    private string scanStatus = "未サーチ";
    private string scanResultsText = "検出局: なし";
    private bool isScanning;
    private bool synchronizingSelection;
    private OneSegAudioTrackOption? selectedAudioTrack;
    private int desiredAudioTrackId = -1;
    private bool captionsEnabled = true;
    private double captionFontSize = 16;
    private bool captionBackgroundEnabled = true;
    private OneSegCaptionPositionOption selectedCaptionPosition;
    private string currentProgramTitle = "番組情報を受信待ち";
    private string currentProgramTime = "--:-- ～ --:--";
    private string nextProgramText = "次番組: --";
    private double programProgress;
    private string emergencyStatus = "緊急警報: 待機中";
    private bool emergencyActive;
    private bool emergencyNotificationsEnabled = true;
    private string broadcastClockStatus = "放送時刻: 受信待ち";
    private string pcrStatus = "PCR: 受信待ち";
    private string networkStatus = "ネットワーク情報: 受信待ち";
    private string dataBroadcastStatus = "データ放送: 受信待ち";
    private string bmlViewerStatus = "BML: 文書を受信待ち";
    private string bmlEntryPath = "";
    private int bmlRevision;
    private IReadOnlyDictionary<string, OneSegBmlResource> bmlResources =
        new Dictionary<string, OneSegBmlResource>(StringComparer.OrdinalIgnoreCase);
    private TimeSpan? broadcastOffset;
    private readonly Queue<DiagnosticWindowSample> diagnosticWindow = new();
    private IPluginRuntimeDiagnostics runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    private OverallStatusKind diagnosticStatusKind = OverallStatusKind.Idle;
    private string diagnosticStatusText = "停止中";
    private string diagnosticPhase = "入力";
    private string diagnosticSummary = "受信を開始すると診断を表示します";
    private string diagnosticRecommendation = "確認: 受信を開始してください";
    private string diagnosticLastUpdated = "—";
    private string diagnosticChannelText = "—";
    private string diagnosticInputText = "—";
    private string diagnosticRateConversionText = "—";
    private string diagnosticRatePathText = "—";
    private string diagnosticPassbandText = "—";
    private string diagnosticChannelLevelText = "—";
    private string diagnosticMerText = "—";
    private string diagnosticFrequencyOffsetText = "—";
    private string diagnosticOfdmText = "—";
    private string diagnosticTmccText = "—";
    private string diagnosticFecText = "—";
    private string diagnosticTransportText = "—";
    private string diagnosticTmccParametersText = "—";
    private OneSegDiagnostics latestDiagnostics = OneSegDiagnostics.Empty;
    private long latestTransportPackets;
    private long latestContinuityErrors;
    private long diagnosticBaselineOfdmSymbols;
    private long diagnosticBaselineTmccFrames;
    private long diagnosticBaselineViterbiBytes;
    private long diagnosticBaselineRsPackets;
    private long diagnosticBaselineRsRejected;
    private long diagnosticBaselineTransportPackets;
    private long diagnosticBaselineContinuityErrors;

    public IReadOnlyList<OneSegPrefecture> Prefectures => OneSegPrefecture.All;
    public ObservableCollection<OneSegChannel> Channels { get; } = [];
    public ObservableCollection<OneSegStation> Stations { get; } = [];
    public ObservableCollection<OneSegProgramInfo> Programs { get; } = [];
    public ObservableCollection<OneSegEventInfo> Events { get; } = [];
    public ObservableCollection<OneSegAudioTrackOption> AudioTracks { get; } = [];
    public ObservableCollection<OneSegConditionalAccessInfo> ConditionalAccess { get; } = [];
    public ObservableCollection<OneSegNetworkInfo> Networks { get; } = [];
    public ObservableCollection<OneSegBroadcasterInfo> Broadcasters { get; } = [];
    public ObservableCollection<OneSegDataModule> DataModules { get; } = [];
    public MediaPlayer? MediaPlayer { get => mediaPlayer; set => SetProperty(ref mediaPlayer, value); }
    public Action<OneSegChannel>? ChannelSelectionRequested { get; set; }
    public Action<OneSegPrefecture, OneSegChannel>? PrefectureSelectionRequested { get; set; }
    public Action<bool>? RecordingChanged { get; set; }
    public Action<string, int, long, long, bool, bool, string, int>? RecordingSettingsChanged { get; set; }
    public Action<int>? VolumeChanged { get; set; }
    public Action? CaptureRequested { get; set; }
    public Action<bool>? ScanRequested { get; set; }
    public Action<OneSegStation>? StationSelectionRequested { get; set; }
    public Action<int>? AudioTrackSelectionRequested { get; set; }
    public Action<bool, double, bool, string>? CaptionSettingsChanged { get; set; }
    public Action<bool>? EmergencyNotificationsChanged { get; set; }

    public OneSegViewModel()
    {
        selectedCaptionPosition = CaptionPositions[^1];
        CaptureCommand = new RelayCommand(() => CaptureRequested?.Invoke());
        ScanCommand = new RelayCommand(() => ScanRequested?.Invoke(false), () => !IsScanning);
        AdditionalScanCommand = new RelayCommand(() => ScanRequested?.Invoke(true), () => !IsScanning);
        PreviousStationCommand = new RelayCommand(() => SelectRelative(-1), () => Stations.Count > 1 && !IsScanning);
        NextStationCommand = new RelayCommand(() => SelectRelative(1), () => Stations.Count > 1 && !IsScanning);
        ResetDiagnosticStatisticsCommand = new RelayCommand(ResetDiagnosticStatistics);
    }

    public IRelayCommand CaptureCommand { get; }
    public IRelayCommand ScanCommand { get; }
    public IRelayCommand AdditionalScanCommand { get; }
    public IRelayCommand PreviousStationCommand { get; }
    public IRelayCommand NextStationCommand { get; }
    public IRelayCommand ResetDiagnosticStatisticsCommand { get; }
    public IReadOnlyList<OneSegCaptionPositionOption> CaptionPositions { get; } =
    [
        new("Top", "上"),
        new("Center", "中央"),
        new("Bottom", "下")
    ];

    public OneSegChannel SelectedChannel
    {
        get => selectedChannel;
        set
        {
            if (!SetProperty(ref selectedChannel, value)) return;
            OnPropertyChanged(nameof(ChannelFrequency));
            if (!synchronizingSelection) ChannelSelectionRequested?.Invoke(value);
        }
    }

    public OneSegPrefecture SelectedPrefecture
    {
        get => selectedPrefecture;
        set
        {
            if (!SetProperty(ref selectedPrefecture, value)) return;
            UpdateCandidates(value, selectedChannel.Number);
            Stations.Clear();
            selectedStation = null;
            ScanStatus = "未サーチ";
            ScanResultsText = "検出局: なし";
            OnPropertyChanged(nameof(SelectedStation));
            if (!synchronizingSelection) PrefectureSelectionRequested?.Invoke(value, selectedChannel);
        }
    }

    public string ChannelFrequency => SelectedChannel.FrequencyText;
    public OneSegStation? SelectedStation
    {
        get => selectedStation;
        set
        {
            if (!SetProperty(ref selectedStation, value)) return;
            NotifyStationCommands();
            RefreshProgramSummary(DateTimeOffset.Now);
            if (value is null || synchronizingSelection) return;
            StationSelectionRequested?.Invoke(value);
        }
    }
    public string ScanStatus
    {
        get => scanStatus;
        set
        {
            if (!SetProperty(ref scanStatus, value)) return;
            OnPropertyChanged(nameof(ScanStatusVisibility));
        }
    }
    public Visibility ScanStatusVisibility => string.IsNullOrWhiteSpace(ScanStatus)
        ? Visibility.Collapsed : Visibility.Visible;
    public string ScanResultsText { get => scanResultsText; set => SetProperty(ref scanResultsText, value); }
    public bool IsScanning
    {
        get => isScanning;
        set
        {
            if (!SetProperty(ref isScanning, value)) return;
            OnPropertyChanged(nameof(SelectionEnabled));
            ScanCommand.NotifyCanExecuteChanged();
            AdditionalScanCommand.NotifyCanExecuteChanged();
            NotifyStationCommands();
        }
    }
    public bool SelectionEnabled => !IsScanning;
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string OfdmLockStatus { get => ofdmLockStatus; private set => SetProperty(ref ofdmLockStatus, value); }
    public string TmccLockStatus { get => tmccLockStatus; private set => SetProperty(ref tmccLockStatus, value); }
    public string MerStatus { get => merStatus; private set => SetProperty(ref merStatus, value); }
    public string RsQualityStatus { get => rsQualityStatus; private set => SetProperty(ref rsQualityStatus, value); }
    public string LockStatus { get => lockStatus; set => SetProperty(ref lockStatus, value); }
    public string SignalStatus { get => signalStatus; set => SetProperty(ref signalStatus, value); }
    public string PacketStatus { get => packetStatus; set => SetProperty(ref packetStatus, value); }
    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => runtimeDiagnostics;
        set => SetProperty(ref runtimeDiagnostics, value ?? NullPluginRuntimeDiagnostics.Instance);
    }
    public OverallStatusKind DiagnosticStatusKind { get => diagnosticStatusKind; private set => SetProperty(ref diagnosticStatusKind, value); }
    public string DiagnosticStatusText { get => diagnosticStatusText; private set => SetProperty(ref diagnosticStatusText, value); }
    public string DiagnosticPhase { get => diagnosticPhase; private set => SetProperty(ref diagnosticPhase, value); }
    public string DiagnosticSummary { get => diagnosticSummary; private set => SetProperty(ref diagnosticSummary, value); }
    public string DiagnosticRecommendation { get => diagnosticRecommendation; private set => SetProperty(ref diagnosticRecommendation, value); }
    public string DiagnosticLastUpdated { get => diagnosticLastUpdated; private set => SetProperty(ref diagnosticLastUpdated, value); }
    public string DiagnosticChannelText { get => diagnosticChannelText; private set => SetProperty(ref diagnosticChannelText, value); }
    public string DiagnosticInputText { get => diagnosticInputText; private set => SetProperty(ref diagnosticInputText, value); }
    public string DiagnosticRateConversionText { get => diagnosticRateConversionText; private set => SetProperty(ref diagnosticRateConversionText, value); }
    public string DiagnosticRatePathText { get => diagnosticRatePathText; private set => SetProperty(ref diagnosticRatePathText, value); }
    public string DiagnosticPassbandText { get => diagnosticPassbandText; private set => SetProperty(ref diagnosticPassbandText, value); }
    public string DiagnosticChannelLevelText { get => diagnosticChannelLevelText; private set => SetProperty(ref diagnosticChannelLevelText, value); }
    public string DiagnosticMerText { get => diagnosticMerText; private set => SetProperty(ref diagnosticMerText, value); }
    public string DiagnosticFrequencyOffsetText { get => diagnosticFrequencyOffsetText; private set => SetProperty(ref diagnosticFrequencyOffsetText, value); }
    public string DiagnosticOfdmText { get => diagnosticOfdmText; private set => SetProperty(ref diagnosticOfdmText, value); }
    public string DiagnosticTmccText { get => diagnosticTmccText; private set => SetProperty(ref diagnosticTmccText, value); }
    public string DiagnosticFecText { get => diagnosticFecText; private set => SetProperty(ref diagnosticFecText, value); }
    public string DiagnosticTransportText { get => diagnosticTransportText; private set => SetProperty(ref diagnosticTransportText, value); }
    public string DiagnosticTmccParametersText { get => diagnosticTmccParametersText; private set => SetProperty(ref diagnosticTmccParametersText, value); }
    public string BroadcastClockStatus { get => broadcastClockStatus; private set => SetProperty(ref broadcastClockStatus, value); }
    public string PcrStatus { get => pcrStatus; private set => SetProperty(ref pcrStatus, value); }
    public string NetworkStatus { get => networkStatus; private set => SetProperty(ref networkStatus, value); }
    public string DataBroadcastStatus { get => dataBroadcastStatus; private set => SetProperty(ref dataBroadcastStatus, value); }
    public string BmlViewerStatus { get => bmlViewerStatus; private set => SetProperty(ref bmlViewerStatus, value); }
    public string BmlEntryPath { get => bmlEntryPath; private set => SetProperty(ref bmlEntryPath, value); }
    public int BmlRevision { get => bmlRevision; private set => SetProperty(ref bmlRevision, value); }
    public string Caption
    {
        get => caption;
        set
        {
            if (!SetProperty(ref caption, value)) return;
            OnPropertyChanged(nameof(CaptionVisibility));
        }
    }
    public Visibility CaptionVisibility => CaptionsEnabled && !string.IsNullOrWhiteSpace(Caption)
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CaptionsEnabled
    {
        get => captionsEnabled;
        set
        {
            if (!SetProperty(ref captionsEnabled, value)) return;
            OnPropertyChanged(nameof(CaptionVisibility));
            OnPropertyChanged(nameof(CaptionAreaVisibility));
            PublishCaptionSettings();
        }
    }
    public double CaptionFontSize
    {
        get => captionFontSize;
        set
        {
            value = 16;
            if (!SetProperty(ref captionFontSize, value)) return;
            OnPropertyChanged(nameof(CaptionAreaHeight));
            PublishCaptionSettings();
        }
    }
    public double CaptionAreaHeight => CaptionFontSize * 3.6 + 12;
    public Visibility CaptionAreaVisibility => CaptionsEnabled
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CaptionBackgroundEnabled
    {
        get => captionBackgroundEnabled;
        set { if (SetProperty(ref captionBackgroundEnabled, value)) PublishCaptionSettings(); }
    }
    public OneSegCaptionPositionOption SelectedCaptionPosition
    {
        get => selectedCaptionPosition;
        set
        {
            if (!SetProperty(ref selectedCaptionPosition, value)) return;
            OnPropertyChanged(nameof(CaptionVerticalAlignment));
            PublishCaptionSettings();
        }
    }
    public VerticalAlignment CaptionVerticalAlignment => SelectedCaptionPosition.Id switch
    {
        "Top" => VerticalAlignment.Top,
        "Center" => VerticalAlignment.Center,
        _ => VerticalAlignment.Bottom
    };
    public OneSegAudioTrackOption? SelectedAudioTrack
    {
        get => selectedAudioTrack;
        set
        {
            if (!SetProperty(ref selectedAudioTrack, value) || value is null || synchronizingSelection) return;
            desiredAudioTrackId = value.Id;
            AudioTrackSelectionRequested?.Invoke(value.Id);
        }
    }
    public string CurrentProgramTitle { get => currentProgramTitle; private set => SetProperty(ref currentProgramTitle, value); }
    public string CurrentProgramTime { get => currentProgramTime; private set => SetProperty(ref currentProgramTime, value); }
    public string NextProgramText { get => nextProgramText; private set => SetProperty(ref nextProgramText, value); }
    public double ProgramProgress { get => programProgress; private set => SetProperty(ref programProgress, value); }
    public string EmergencyStatus { get => emergencyStatus; private set => SetProperty(ref emergencyStatus, value); }
    public bool EmergencyActive
    {
        get => emergencyActive;
        private set
        {
            if (!SetProperty(ref emergencyActive, value)) return;
            OnPropertyChanged(nameof(EmergencyVisibility));
        }
    }
    public Visibility EmergencyVisibility => EmergencyActive ? Visibility.Visible : Visibility.Collapsed;
    public bool EmergencyNotificationsEnabled
    {
        get => emergencyNotificationsEnabled;
        set
        {
            if (SetProperty(ref emergencyNotificationsEnabled, value))
                EmergencyNotificationsChanged?.Invoke(value);
        }
    }
    public string RecordingStatus { get => recordingStatus; set => SetProperty(ref recordingStatus, value); }
    public string CaptureStatus { get => captureStatus; set => SetProperty(ref captureStatus, value); }
    public bool Recording
    {
        get => recording;
        set { if (SetProperty(ref recording, value)) RecordingChanged?.Invoke(value); }
    }
    internal void SetRecordingState(bool value)
    {
        if (recording == value) return;
        recording = value;
        OnPropertyChanged(nameof(Recording));
    }
    private string recordingDirectory = "";
    private int recordingSegmentMinutes;
    private long recordingMaxBytes;
    private long recordingStorageLimitBytes;
    private bool autoRecordOnStart;
    private bool recordingSidecarEnabled = true;
    private string iqCaptureDirectory = "";
    private int iqCaptureSeconds = 5;
    public string RecordingDirectory
    {
        get => recordingDirectory;
        set { if (SetProperty(ref recordingDirectory, value)) NotifyRecordingSettingsChanged(); }
    }
    public int RecordingSegmentMinutes
    {
        get => recordingSegmentMinutes;
        set { value = Math.Clamp(value, 0, 24 * 60); if (SetProperty(ref recordingSegmentMinutes, value)) NotifyRecordingSettingsChanged(); }
    }
    public long RecordingMaxBytes
    {
        get => recordingMaxBytes;
        set { value = Math.Clamp(value, 0, 1_000_000_000_000L); if (SetProperty(ref recordingMaxBytes, value)) NotifyRecordingSettingsChanged(); }
    }
    public long RecordingStorageLimitBytes
    {
        get => recordingStorageLimitBytes;
        set { value = Math.Clamp(value, 0, 10_000_000_000_000L); if (SetProperty(ref recordingStorageLimitBytes, value)) NotifyRecordingSettingsChanged(); }
    }
    public bool AutoRecordOnStart { get => autoRecordOnStart; set { if (SetProperty(ref autoRecordOnStart, value)) NotifyRecordingSettingsChanged(); } }
    public bool RecordingSidecarEnabled { get => recordingSidecarEnabled; set { if (SetProperty(ref recordingSidecarEnabled, value)) NotifyRecordingSettingsChanged(); } }
    public string IqCaptureDirectory { get => iqCaptureDirectory; set { if (SetProperty(ref iqCaptureDirectory, value)) NotifyRecordingSettingsChanged(); } }
    public int IqCaptureSeconds { get => iqCaptureSeconds; set { int n=Math.Clamp(value,1,60); if(SetProperty(ref iqCaptureSeconds,n)) NotifyRecordingSettingsChanged(); } }

    private void NotifyRecordingSettingsChanged()
    {
        if (!synchronizingSelection)
            RecordingSettingsChanged?.Invoke(RecordingDirectory, RecordingSegmentMinutes,
                RecordingMaxBytes, RecordingStorageLimitBytes, AutoRecordOnStart,
                RecordingSidecarEnabled, IqCaptureDirectory, IqCaptureSeconds);
    }
    public int Volume
    {
        get => volume;
        set { value = 100; if (SetProperty(ref volume, value)) VolumeChanged?.Invoke(value); }
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "ワンセグ",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndClearData(
            "ワンセグ",
            () =>
            {
                ResetServiceData();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "ワンセグ",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { },
            () =>
            {
                ResetServiceData();
            });
    }
}
