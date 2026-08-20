using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.WiSun.Dsp;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.ViewModels;

public sealed partial class WiSunViewModel : ObservableObject, IFrequencyOverlayProvider
{
    private readonly WiSunPluginModule _module;
    private readonly WiSunAddressResolver _addressResolver = new();
    private readonly Dispatcher _dispatcher;
    [ObservableProperty] private WiSunPacketFrame? _lastPacket;
    [ObservableProperty] private string _statusText = "Wi-SUN 復調動作中";
    [ObservableProperty] private int _selectedTabIndex;
    private double _frequencyMhz = 922.4;
    private double _frequencyStepMhz = 0.1;
    private float _squelchThresholdDbm = -125.0f;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiverStatusKind))]
    private bool _isReceiverEnabled = true;
    private int _diagnosticRefreshQueued;
    private readonly ConcurrentQueue<WiSunPacketFrame> _pendingPacketFrames = new();
    private int _pendingPacketCount;
    private int _packetFlushQueued;
    private bool _rebuildingChannelSelections;
    private string? _customFrequencyMHzText;
    private string? _customSfdHexText;
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    private string _diagnosticLastUpdated = "未更新";
    private readonly Queue<DiagnosticWindowSample> _diagnosticWindow = new();
    private DateTimeOffset? _lastCrcOkAt;
    [ObservableProperty] private WiSunPanReceptionGroup? _selectedPanGroup;
    [ObservableProperty] private WiSunPacketFrame? _selectedTimelinePacket;
    [ObservableProperty] private string _panSearchText = string.Empty;
    [ObservableProperty] private string _packetSearchText = string.Empty;

    public event EventHandler? FrequencyOverlaysChanged;

    public ObservableCollection<WiSunPacketFrame> Packets { get; } = new();
    public ObservableCollection<WiSunPanReceptionGroup> RecentPanGroups { get; } = new();
    public ObservableCollection<WiSunPacketFrame> RecentCommunications { get; } = new();
    public ObservableCollection<string> DiagnosticLogs { get; } = new();
    public ObservableCollection<WiSunChannelSelectionItem> ChannelSelections { get; } = new();

    public long TotalSyncAttempts => _module.TotalSyncAttempts;
    public long TotalRfBursts => _module.TotalRfBursts;
    public long TotalPreambleMatches => _module.TotalPreambleMatches;
    public long TotalSfdMatches => _module.TotalSfdMatches;
    public long TotalPhrValid => _module.TotalPhrValid;
    public long TotalPayloadRead => _module.TotalPayloadRead;
    public long TotalFramesPublished => _module.TotalFramesPublished;
    public long TotalCrcOk => _module.TotalCrcOk;
    public long TotalCrcNg => _module.TotalCrcNg;
    public string LastSyncRawDescription => _module.Demodulator.TotalSfdMatches == 0
        ? "--"
        : $"PRE[{_module.Demodulator.LastPreambleByteCount}B] " +
          $"{(_module.Demodulator.LastPreambleByteCount == 0 ? "--" : _module.Demodulator.LastPreambleRawHex)}  |  " +
          $"SFD 0x{_module.Demodulator.LastSfdWord:X4}  |  " +
          $"RAW {_module.Demodulator.LastPreambleRawHex}{_module.Demodulator.LastSfdWord:X4}";
    public string SymbolTimingDescription =>
        $"{_module.Demodulator.LastRecoveredSamplesPerBit:F4} sample/bit  |  " +
        $"{_module.Demodulator.LastClockErrorPpm:+0;-0;0} ppm";

    public double CrcPassRatePercent => (TotalCrcOk + TotalCrcNg) > 0 ? (TotalCrcOk * 100.0 / (TotalCrcOk + TotalCrcNg)) : double.NaN;
    public double CrcErrorRatePercent => (TotalCrcOk + TotalCrcNg) > 0 ? (TotalCrcNg * 100.0 / (TotalCrcOk + TotalCrcNg)) : double.NaN;
    public string CrcPassRateText => double.IsFinite(CrcPassRatePercent) ? $"{CrcPassRatePercent:F1}%" : "—";
    public string CrcErrorRateText => double.IsFinite(CrcErrorRatePercent) ? $"{CrcErrorRatePercent:F1}%" : "—";

    public OverallStatusKind DiagnosticStatusKind => EvaluateDiagnostic().Kind;
    public string DiagnosticStatusText => EvaluateDiagnostic().Status;
    public string DiagnosticPhase => EvaluateDiagnostic().Phase;
    public string DiagnosticSummary => EvaluateDiagnostic().Summary;
    public string DiagnosticRecommendation => EvaluateDiagnostic().Recommendation;
    public string DiagnosticLastUpdated => _diagnosticLastUpdated;
    public string DiagnosticInputText
    {
        get
        {
            int rate = _module.Demodulators.Select(value => value.LastSourceInputSampleRateHz)
                .DefaultIfEmpty(0).Max();
            return SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatSampleRate(rate);
        }
    }
    public string DiagnosticRateConversionText
    {
        get
        {
            WiSunDemodulator[] active = _module.Demodulators
                .Where(value => value.LastSourceInputSampleRateHz > 0).ToArray();
            if (active.Length == 0) return "—";
            bool converted = active.Any(value =>
                value.LastSourceInputSampleRateHz != value.LastInputSampleRateHz);
            return SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(converted,
                active.Any(value => value.UsesHostChannelRateConversion) ? "標準チャネル" : "プラグイン内部");
        }
    }
    public string DiagnosticRatePathText
    {
        get
        {
            string[] paths = _module.Demodulators
                .Where(value => value.LastSourceInputSampleRateHz > 0)
                .Select(value => SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
                    value.LastSourceInputSampleRateHz,
                    value.LastIntermediateSampleRateHz,
                    value.LastInputSampleRateHz))
                .Distinct(StringComparer.Ordinal).ToArray();
            return paths.Length == 0 ? "—" : string.Join(" / ", paths);
        }
    }
    public string DiagnosticProfileText => _module.Settings.PhyProfile switch
    {
        WiSunPhyProfile.Custom => $"Custom / {_module.Settings.CustomBitRateBps / 1_000.0:F1} kbit/s",
        _ => _module.Settings.PhyProfile.ToString()
    };
    public string DiagnosticPassbandText => _module.ChannelRequests.Count == 0
        ? "—" : $"帯域内 / {_module.ChannelRequests.Count:N0} channel";
    public string DiagnosticSignalLevelText => _module.SignalLevelDbm is { } dbm && float.IsFinite(dbm)
        ? $"{dbm:F1} dBm"
        : (double.IsFinite(_module.DiagnosticInputLevelDbfs) ? $"{_module.DiagnosticInputLevelDbfs:F1} dBFS" : "—");
    public string DiagnosticNoiseFloorText => _module.NoiseFloorDbm is { } dbm && float.IsFinite(dbm)
        ? $"{dbm:F1} dBm（監視ch最大）"
        : (double.IsFinite(_module.DiagnosticNoiseFloorDbfs) ? $"{_module.DiagnosticNoiseFloorDbfs:F1} dBFS（監視ch最大）" : "—");
    public string DiagnosticLastReceptionText => Packets.FirstOrDefault() is { } packet
        ? $"推定SNR {(float.IsFinite(packet.SnrDb) ? $"{packet.SnrDb:F1} dB" : "—")} / ピーク {packet.PeakDbm:F1} dBm"
        : "—";
    public string DiagnosticClockErrorText => TotalSfdMatches > 0
        ? $"{_module.Demodulator.LastClockErrorPpm:+0;-0;0} ppm" : "—";
    public string DiagnosticChannelText
    {
        get
        {
            long[] frequencies = _module.ChannelRequests
                .Select(value => value.CenterFrequencyHz)
                .OrderBy(value => value)
                .ToArray();
            if (frequencies.Length == 0) return "監視 0 ch";
            if (frequencies.Length == 1)
                return $"監視 1 ch / {frequencies[0] / 1_000_000.0:F3} MHz";
            return $"監視 {frequencies.Length:N0} ch / {frequencies[0] / 1_000_000.0:F3}～{frequencies[^1] / 1_000_000.0:F3} MHz";
        }
    }

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays
    {
        get
        {
            WiSunPhyProfile profile = _module.Settings.PhyProfile;
            int bandwidthHz = WiSunPluginModule.TuningBandwidthHz(profile, _module.Settings.CustomBitRateBps);

            if (profile == WiSunPhyProfile.Custom)
            {
                long freqHz = _module.Settings.CustomFrequencyHz;
                return [
                    new FrequencyOverlayItem(
                        "wisun-custom-01",
                        freqHz,
                        bandwidthHz,
                        $"{freqHz / 1e6:F3} MHz",
                        true,
                        PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
                        PluginReceiverBandColors.WithAlpha(0x80, PluginReceiverBandColors.Primary),
                        "#FFFFFFFF",
                        0)
                ];
            }

            bool isHan = profile == WiSunPhyProfile.HanBRoute;
            HashSet<int> selectedChannels = (isHan
                ? _module.Settings.HanChannels
                : _module.Settings.FanChannels).ToHashSet();

            int lane = 0;
            string prefix = isHan ? "wisun-han" : "wisun-fan";
            string fill = PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary);
            string stroke = PluginReceiverBandColors.WithAlpha(0x80, PluginReceiverBandColors.Primary);

            return WiSunPluginModule.GetChannelOptions(profile)
                .Select(option =>
                {
                    bool isSelected = selectedChannels.Contains(option.Channel);
                    return new FrequencyOverlayItem(
                        $"{prefix}-{option.Channel}",
                        option.FrequencyHz,
                        bandwidthHz,
                        option.Channel.ToString(),
                        isSelected,
                        isSelected ? fill : "#00000000",
                        stroke,
                        "#FFFFFFFF",
                        lane);
                })
                .ToArray();
        }
    }

    public bool AllowCrcErrorPackets
    {
        get => !_module.RejectInvalidFcs;
        set
        {
            if (_module.RejectInvalidFcs == value)
            {
                _module.RejectInvalidFcs = !value;
                OnPropertyChanged();
            }
        }
    }

    public sealed record WiSunPhyOption(WiSunPhyProfile Profile, string DisplayName);

    public IReadOnlyList<WiSunPhyOption> PhyOptions { get; } =
    [
        new(WiSunPhyProfile.FanMode1b, "FAN Mode #1b (50 kbps)"),
        new(WiSunPhyProfile.FanMode2, "FAN Mode #2 (100 kbps)"),
        new(WiSunPhyProfile.FanMode3, "FAN Mode #3 (150 kbps)"),
        new(WiSunPhyProfile.FanMode4, "FAN Mode #4 (200 kbps)"),
        new(WiSunPhyProfile.FanMode5, "FAN Mode #5 (300 kbps)"),
        new(WiSunPhyProfile.HanBRoute, "HAN A,Bルート (100 kbps)"),
        new(WiSunPhyProfile.Custom, "カスタム (周波数/速度/SFD指定)")
    ];

    public bool IsCustomPhy => _module.Settings.PhyProfile == WiSunPhyProfile.Custom;
    public bool IsStandardPhy => !IsCustomPhy;

    public string CustomFrequencyMHzText
    {
        get => _customFrequencyMHzText ?? (_module.Settings.CustomFrequencyHz / 1e6).ToString("F3");
        set
        {
            _customFrequencyMHzText = value;
            OnPropertyChanged();
            if (double.TryParse(value, out double mhz))
            {
                long hz = (long)Math.Round(mhz * 1e6);
                if (hz >= 100_000_000L && hz <= 2_500_000_000L && hz != _module.Settings.CustomFrequencyHz)
                {
                    _ = _module.UpdateSettingsAsync(_module.Settings with { CustomFrequencyHz = hz });
                    SynchronizeFrequency(hz);
                }
            }
        }
    }

    public sealed record WiSunBitRateOption(int BitRateBps, string DisplayName);
    public IReadOnlyList<WiSunBitRateOption> BitRateOptions { get; } =
    [
        new(50_000, "50 kbps"),
        new(100_000, "100 kbps"),
        new(150_000, "150 kbps"),
        new(200_000, "200 kbps"),
        new(300_000, "300 kbps")
    ];

    public WiSunBitRateOption SelectedBitRateOption
    {
        get => BitRateOptions.FirstOrDefault(opt => opt.BitRateBps == _module.Settings.CustomBitRateBps) ?? BitRateOptions[0];
        set
        {
            if (value != null && _module.Settings.CustomBitRateBps != value.BitRateBps)
            {
                _ = _module.UpdateSettingsAsync(_module.Settings with { CustomBitRateBps = value.BitRateBps });
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedChannelSummary));
            }
        }
    }

    public string CustomSfdHex
    {
        get => _customSfdHexText ?? _module.Settings.CustomSfdHex;
        set
        {
            _customSfdHexText = value;
            OnPropertyChanged();
            if (!string.IsNullOrWhiteSpace(value))
            {
                string cleaned = value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                if (ushort.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out _))
                {
                    if (!string.Equals(_module.Settings.CustomSfdHex, cleaned, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = _module.UpdateSettingsAsync(_module.Settings with { CustomSfdHex = cleaned });
                        OnPropertyChanged(nameof(SelectedChannelSummary));
                    }
                }
            }
        }
    }

    public bool EnableRawBurstLog
    {
        get => _module.Settings.EnableRawBurstLog;
        set
        {
            if (_module.Settings.EnableRawBurstLog != value)
            {
                _ = _module.UpdateSettingsAsync(_module.Settings with { EnableRawBurstLog = value });
                OnPropertyChanged();
            }
        }
    }

    public WiSunPhyOption SelectedPhyOption
    {
        get => PhyOptions.FirstOrDefault(opt => opt.Profile == _module.Settings.PhyProfile) ?? PhyOptions[0];
        set
        {
            if (value is not null && _module.Settings.PhyProfile != value.Profile)
            {
                SelectPhy(value.Profile);
            }
        }
    }

    public bool IsFanSelected
    {
        get => _module.Settings.PhyProfile == WiSunPhyProfile.FanMode1b;
        set { if (value && !IsFanSelected) SelectPhy(WiSunPhyProfile.FanMode1b); }
    }

    public bool IsHanSelected
    {
        get => _module.Settings.PhyProfile == WiSunPhyProfile.HanBRoute;
        set { if (value && !IsHanSelected) SelectPhy(WiSunPhyProfile.HanBRoute); }
    }

    public string SelectedChannelSummary
    {
        get
        {
            if (_module.Settings.PhyProfile == WiSunPhyProfile.Custom)
                return $"カスタム {_module.Settings.CustomBitRateBps / 1_000}k {_module.Settings.CustomFrequencyHz / 1_000_000.0:F3}MHz (SFD:{_module.Settings.CustomSfdHex})";

            int[] channels = _module.Settings.PhyProfile == WiSunPhyProfile.HanBRoute
                ? _module.Settings.HanChannels
                : _module.Settings.FanChannels;
            return $"{channels.Length} ch / {string.Join(", ", channels.Select(value => $"Ch {value}"))}";
        }
    }

    public string WiSunChannelRange
    {
        get
        {
            if (ChannelSelections.Count == 0) return string.Empty;
            int min = ChannelSelections.Min(item => item.Channel);
            int max = ChannelSelections.Max(item => item.Channel);
            return $"Ch {min}～{max}";
        }
    }

    public sealed record WiSunStepOption(string DisplayName, double Value);

    public sealed partial class WiSunChannelSelectionItem : ObservableObject
    {
        [ObservableProperty] private bool _isSelected;

        public WiSunChannelSelectionItem(
            WiSunPluginModule.WiSunChannelOption option,
            bool isSelected)
        {
            Channel = option.Channel;
            FrequencyHz = option.FrequencyHz;
            Label = option.Channel.ToString();
            _isSelected = isSelected;
        }

        public int Channel { get; }
        public long FrequencyHz { get; }
        public string Label { get; }
    }

    public IReadOnlyList<WiSunStepOption> StepOptions { get; } =
    [
        new("10 kHz", 0.010),
        new("50 kHz", 0.050),
        new("100 kHz", 0.100),
        new("200 kHz", 0.200),
        new("500 kHz", 0.500)
    ];

    public IReadOnlyList<PluginProfileDescriptor> Profiles => _module.Profiles;

    public string? SelectedProfileId
    {
        get => _module.SelectedProfileId;
        set
        {
            if (_module.SelectedProfileId != value && !string.IsNullOrEmpty(value))
            {
                _ = Task.Run(async () =>
                {
                    try { await _module.SelectProfileAsync(value, default).ConfigureAwait(false); }
                    catch { }
                });
                OnPropertyChanged();
            }
        }
    }

    public WiSunViewModel(WiSunPluginModule module)
    {
        _module = module;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _frequencyMhz = module.Settings.FrequencyHz / 1e6;
        _frequencyStepMhz = module.Settings.FrequencyStepHz / 1e6;
        _squelchThresholdDbm = module.Settings.SquelchThresholdDbm;
        _isReceiverEnabled = module.Settings.IsReceiverEnabled;
        RebuildChannelSelections();

        _module.OnDiagnosticLog += log =>
        {
            DispatchToOwner(() =>
            {
                DiagnosticLogs.Insert(0, log);
                while (DiagnosticLogs.Count > 200) DiagnosticLogs.RemoveAt(DiagnosticLogs.Count - 1);
                RefreshDiagnosticProperties();
            });
        };
        _module.OnDiagnosticCountersChanged += QueueDiagnosticRefresh;
    }

    public void ResetDiagnosticCounters()
    {
        _module.ResetDiagnosticCounters();
        _diagnosticWindow.Clear();
        _lastCrcOkAt = null;
        RefreshDiagnosticProperties();
    }

    public void ClearDiagnosticLogs() => DiagnosticLogs.Clear();

    public void RefreshDiagnosticProperties()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        UpdateDiagnosticWindow(now);
        _diagnosticLastUpdated = _module.DiagnosticLastMeasuredAt is DateTimeOffset measuredAt
            ? measuredAt.ToLocalTime().ToString("HH:mm:ss") : "未更新";
        OnPropertyChanged(nameof(TotalSyncAttempts));
        OnPropertyChanged(nameof(TotalRfBursts));
        OnPropertyChanged(nameof(TotalPreambleMatches));
        OnPropertyChanged(nameof(TotalSfdMatches));
        OnPropertyChanged(nameof(TotalPhrValid));
        OnPropertyChanged(nameof(TotalPayloadRead));
        OnPropertyChanged(nameof(TotalFramesPublished));
        OnPropertyChanged(nameof(TotalCrcOk));
        OnPropertyChanged(nameof(TotalCrcNg));
        OnPropertyChanged(nameof(CrcPassRatePercent));
        OnPropertyChanged(nameof(CrcErrorRatePercent));
        OnPropertyChanged(nameof(CrcPassRateText));
        OnPropertyChanged(nameof(CrcErrorRateText));
        OnPropertyChanged(nameof(LastSyncRawDescription));
        OnPropertyChanged(nameof(SymbolTimingDescription));
        OnPropertyChanged(nameof(DiagnosticStatusKind));
        OnPropertyChanged(nameof(DiagnosticStatusText));
        OnPropertyChanged(nameof(DiagnosticPhase));
        OnPropertyChanged(nameof(DiagnosticSummary));
        OnPropertyChanged(nameof(DiagnosticRecommendation));
        OnPropertyChanged(nameof(DiagnosticLastUpdated));
        OnPropertyChanged(nameof(DiagnosticChannelText));
        OnPropertyChanged(nameof(DiagnosticInputText));
        OnPropertyChanged(nameof(DiagnosticRateConversionText));
        OnPropertyChanged(nameof(DiagnosticRatePathText));
        OnPropertyChanged(nameof(DiagnosticProfileText));
        OnPropertyChanged(nameof(DiagnosticPassbandText));
        OnPropertyChanged(nameof(DiagnosticSignalLevelText));
        OnPropertyChanged(nameof(DiagnosticNoiseFloorText));
        OnPropertyChanged(nameof(DiagnosticLastReceptionText));
        OnPropertyChanged(nameof(DiagnosticClockErrorText));
    }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "Wi-SUN",
            async () =>
            {
                await _module.ResetSettingsAsync();
            },
            () =>
            {
                SelectedPhyOption = PhyOptions.FirstOrDefault(p => p.Profile == _module.Settings.PhyProfile) ?? PhyOptions[0];
                IsReceiverEnabled = true;
            });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        PluginResetHelper.ConfirmAndClearData(
            "Wi-SUN",
            () =>
            {
                Packets.Clear();
                RecentPanGroups.Clear();
                RecentCommunications.Clear();
                DiagnosticLogs.Clear();
                ResetDiagnosticCounters();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "Wi-SUN",
            async () =>
            {
                await _module.ResetSettingsAsync();
            },
            () =>
            {
                SelectedPhyOption = PhyOptions.FirstOrDefault(p => p.Profile == _module.Settings.PhyProfile) ?? PhyOptions[0];
                IsReceiverEnabled = true;
            },
            () =>
            {
                Packets.Clear();
                RecentPanGroups.Clear();
                RecentCommunications.Clear();
                DiagnosticLogs.Clear();
                ResetDiagnosticCounters();
            });
    }
}
