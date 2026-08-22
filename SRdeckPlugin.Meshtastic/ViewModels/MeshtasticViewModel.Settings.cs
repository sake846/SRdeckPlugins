using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    [ObservableProperty] private long _meshtasticPayloadCrcOkCount;
    [ObservableProperty] private long _meshtasticPayloadCrcErrorCount;
    [ObservableProperty] private long _meshtasticSyncFailureCount;
    [ObservableProperty] private long _meshtasticSfdFailureCount;
    [ObservableProperty] private long _meshtasticHeaderFailureCount;
    [ObservableProperty] private long _meshtasticPayloadFailureCount;
    [ObservableProperty] private string _meshtasticPassbandStatus = "対象帯域: 待機中";
    [ObservableProperty] private string _meshtasticRateConversionText = "—";
    [ObservableProperty] private string _meshtasticRatePathText = "—";
    [ObservableProperty] private string _meshtasticLastSignalStatus = "最終信号: -";
    [ObservableProperty] private string _meshtasticLastFailureStatus = "直近の失敗: -";
    [ObservableProperty] private string _meshtasticFrequencyCorrectionText = "—";

    [ObservableProperty] private int _meshtasticHistoryDisplayLimit = 10_000;
    [ObservableProperty] private int _meshtasticHistoryRetentionDays = 90;
    [ObservableProperty] private MeshtasticRegion _selectedMeshtasticRegion = MeshtasticRegion.JP;
    [ObservableProperty] private int _meshtasticRadioChannel = MeshtasticJpLongFastProfile.DefaultChannel;
    [ObservableProperty] private string _meshtasticRadioChannels250 = MeshtasticJpLongFastProfile.DefaultChannel.ToString();
    [ObservableProperty] private string _meshtasticRadioChannels125 = MeshtasticJpLongFastProfile.DefaultChannel.ToString();
    public ObservableCollection<MeshtasticSlotSelectionItem> MeshtasticSlots250 { get; } = new();
    public ObservableCollection<MeshtasticSlotSelectionItem> MeshtasticSlots125 { get; } = new();
    public IReadOnlyList<MeshtasticRegion> MeshtasticRegions { get; } = Enum.GetValues<MeshtasticRegion>();
    [ObservableProperty] private MeshtasticModemPreset _selectedMeshtasticModemPreset = MeshtasticModemPreset.LongFast;
    private MeshtasticModemPreset _lastSpecifiedMeshtasticPreset = MeshtasticModemPreset.LongFast;
    [ObservableProperty] private bool _meshtasticDiscoveryUses250Khz = true;
    [ObservableProperty] private bool _meshtasticDiscoveryUses125Khz = true;
    private bool _isUpdatingMeshtasticDiscoveryBandwidths;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeshtasticSpecifiedMonitorMode))]
    private bool _isMeshtasticDiscoveryMode;
    public bool IsMeshtasticSpecifiedMonitorMode
    {
        get => !IsMeshtasticDiscoveryMode;
        set
        {
            if (value && IsMeshtasticDiscoveryMode)
            {
                IsMeshtasticDiscoveryMode = false;
            }
        }
    }
    public MeshtasticModemPreset SelectedMeshtasticSpecifiedModemPreset
    {
        get => IsMeshtasticDiscoveryMode ? _lastSpecifiedMeshtasticPreset : SelectedMeshtasticModemPreset;
        set
        {
            if (!MeshtasticJpLongFastProfile.IsAutoSf(value))
            {
                _lastSpecifiedMeshtasticPreset = value;
                if (!IsMeshtasticDiscoveryMode)
                {
                    SelectedMeshtasticModemPreset = value;
                }
            }
            OnPropertyChanged(nameof(SelectedMeshtasticSpecifiedModemPreset));
        }
    }
    public IReadOnlyList<MeshtasticModemPreset> MeshtasticSpecifiedModemPresets { get; } =
    [
        MeshtasticModemPreset.LongFast,
        MeshtasticModemPreset.LongModerate,
        MeshtasticModemPreset.LongSlow,
        MeshtasticModemPreset.MediumSlow,
        MeshtasticModemPreset.MediumFast,
        MeshtasticModemPreset.ShortSlow,
        MeshtasticModemPreset.ShortFast
    ];
    public bool MeshtasticUses250Khz => MeshtasticJpLongFastProfile.GetChannelProfiles(SelectedMeshtasticModemPreset).Any(value => value.BandwidthHz == 250_000);
    public bool MeshtasticUses125Khz => MeshtasticJpLongFastProfile.GetChannelProfiles(SelectedMeshtasticModemPreset).Any(value => value.BandwidthHz == 125_000);
    public string Meshtastic250SlotRange => $"slot 1～{MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion).GetMaximumChannel(250_000)}";
    public string Meshtastic125SlotRange => $"slot 1～{MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion).GetMaximumChannel(125_000)}";
    [ObservableProperty] private string _meshtasticChannelSettingStatus = "LongFast既定チャンネル";
    private bool _isLoadingMeshtasticSettings;
    private bool _isUpdatingMeshtasticSlotSelection;

    private PersistedPluginSettings LoadPersistedPluginSettings() => _meshtasticSettingsService.Load();

    private void SaveMeshtasticSettings()
    {
        if (_hostContext is null || _isLoadingMeshtasticSettings) return;
        var settings = new PersistedPluginSettings(
            SelectedMeshtasticRegion,
            SelectedMeshtasticModemPreset,
            IsMeshtasticDiscoveryMode,
            MeshtasticRadioChannel,
            MeshtasticRadioChannels250,
            MeshtasticRadioChannels125,
            Math.Clamp(MeshtasticHistoryDisplayLimit, 100, 100_000),
            Math.Clamp(MeshtasticHistoryRetentionDays, 1, 3650),
            _lastSpecifiedMeshtasticPreset,
            GeoMapStateStore.GetState("meshtastic"));
        _meshtasticSettingsService.Save(settings);
    }

    private void LoadMeshtasticSettings()
    {
        _isLoadingMeshtasticSettings = true;
        PersistedPluginSettings settings = LoadPersistedPluginSettings();
        SelectedMeshtasticRegion = settings.Region;
        _lastSpecifiedMeshtasticPreset = MeshtasticJpLongFastProfile.IsAutoSf(settings.LastSpecifiedModemPreset)
            ? MeshtasticModemPreset.LongFast
            : settings.LastSpecifiedModemPreset;
        SelectedMeshtasticModemPreset = settings.ModemPreset;
        IsMeshtasticDiscoveryMode = settings.IsDiscoveryMode;
        if (IsMeshtasticDiscoveryMode)
        {
            if (!MeshtasticJpLongFastProfile.IsAutoSf(SelectedMeshtasticModemPreset))
                SelectedMeshtasticModemPreset = MeshtasticModemPreset.AutoSf250And125;
            SyncMeshtasticDiscoveryBandwidths(SelectedMeshtasticModemPreset);
            EnsureValidDiscoverySlotSelections();
        }
        else
        {
            if (MeshtasticJpLongFastProfile.IsAutoSf(SelectedMeshtasticModemPreset))
                SelectedMeshtasticModemPreset = _lastSpecifiedMeshtasticPreset;
            else
                _lastSpecifiedMeshtasticPreset = SelectedMeshtasticModemPreset;
        }
        MeshtasticRadioChannel = settings.RadioChannel;
        MeshtasticRadioChannels250 = settings.RadioChannels250;
        MeshtasticRadioChannels125 = settings.RadioChannels125;
        MeshtasticHistoryDisplayLimit = Math.Clamp(settings.HistoryDisplayLimit, 100, 100_000);
        MeshtasticHistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 1, 3650);
        RebuildMeshtasticSlotSelections();
        _isLoadingMeshtasticSettings = false;
        ApplyMeshtasticChannelSettings();
    }

    partial void OnMeshtasticHistoryDisplayLimitChanged(int value)
    {
        if (_isLoadingMeshtasticSettings) return;
        int clamped = Math.Clamp(value, 100, 100_000);
        if (value != clamped)
        {
            _isLoadingMeshtasticSettings = true;
            MeshtasticHistoryDisplayLimit = clamped;
            _isLoadingMeshtasticSettings = false;
        }
        SaveMeshtasticHistorySettings();
    }
    partial void OnMeshtasticHistoryRetentionDaysChanged(int value)
    {
        if (_isLoadingMeshtasticSettings) return;
        int clamped = Math.Clamp(value, 1, 3650);
        if (value != clamped)
        {
            _isLoadingMeshtasticSettings = true;
            MeshtasticHistoryRetentionDays = clamped;
            _isLoadingMeshtasticSettings = false;
        }
        PruneMeshtasticHistoryFile();
        SaveMeshtasticHistorySettings();
    }

    private void SaveMeshtasticHistorySettings()
    {
        if (_isLoadingMeshtasticSettings) return;
        SaveMeshtasticSettings();
    }
    partial void OnMeshtasticRadioChannelChanged(int value) => ApplyMeshtasticChannelSettings();
    partial void OnMeshtasticRadioChannels250Changed(string value)
    {
        if (_isUpdatingMeshtasticSlotSelection) return;
        _isUpdatingMeshtasticSlotSelection = true;
        try { SyncMeshtasticSlotSelections(MeshtasticSlots250, value); }
        finally { _isUpdatingMeshtasticSlotSelection = false; }
        ApplyMeshtasticChannelSettings();
    }
    partial void OnMeshtasticRadioChannels125Changed(string value)
    {
        if (_isUpdatingMeshtasticSlotSelection) return;
        _isUpdatingMeshtasticSlotSelection = true;
        try { SyncMeshtasticSlotSelections(MeshtasticSlots125, value); }
        finally { _isUpdatingMeshtasticSlotSelection = false; }
        ApplyMeshtasticChannelSettings();
    }
    partial void OnIsMeshtasticDiscoveryModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMeshtasticSpecifiedMonitorMode));
        OnPropertyChanged(nameof(SelectedMeshtasticSpecifiedModemPreset));
        if (_isLoadingMeshtasticSettings) return;

        if (value)
        {
            if (!MeshtasticJpLongFastProfile.IsAutoSf(SelectedMeshtasticModemPreset))
                _lastSpecifiedMeshtasticPreset = SelectedMeshtasticModemPreset;

            EnsureValidDiscoverySlotSelections();

            MeshtasticModemPreset discoveryPreset = (MeshtasticDiscoveryUses250Khz, MeshtasticDiscoveryUses125Khz) switch
            {
                (true, true) => MeshtasticModemPreset.AutoSf250And125,
                (true, false) => MeshtasticModemPreset.AutoSf250,
                _ => MeshtasticModemPreset.AutoSf125
            };

            SelectedMeshtasticModemPreset = discoveryPreset;
        }
        else
        {
            SelectedMeshtasticModemPreset = _lastSpecifiedMeshtasticPreset;
        }

        OnPropertyChanged(nameof(Meshtastic250SlotRange));
        OnPropertyChanged(nameof(Meshtastic125SlotRange));
        ApplyMeshtasticChannelSettings();
    }

    partial void OnMeshtasticDiscoveryUses250KhzChanged(bool value) =>
        UpdateMeshtasticDiscoveryPreset(250_000);

    partial void OnMeshtasticDiscoveryUses125KhzChanged(bool value) =>
        UpdateMeshtasticDiscoveryPreset(125_000);

    private void EnsureValidDiscoverySlotSelections()
    {
        MeshtasticRegionProfile regionProfile = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion);
        if (MeshtasticDiscoveryUses250Khz && !TryParseMeshtasticRadioChannels(MeshtasticRadioChannels250, out _, out _))
        {
            int max250 = regionProfile.GetMaximumChannel(250_000);
            MeshtasticRadioChannels250 = Math.Clamp(MeshtasticRadioChannel, 1, max250).ToString();
        }
        if (MeshtasticDiscoveryUses125Khz && !TryParseMeshtasticRadioChannels(MeshtasticRadioChannels125, out _, out _))
        {
            int max125 = regionProfile.GetMaximumChannel(125_000);
            MeshtasticRadioChannels125 = Math.Clamp(MeshtasticRadioChannel, 1, max125).ToString();
        }
    }

    private void UpdateMeshtasticDiscoveryPreset(int changedBandwidthHz)
    {
        if (_isLoadingMeshtasticSettings || _isUpdatingMeshtasticDiscoveryBandwidths || !IsMeshtasticDiscoveryMode)
            return;

        if (!MeshtasticDiscoveryUses250Khz && !MeshtasticDiscoveryUses125Khz)
        {
            _isUpdatingMeshtasticDiscoveryBandwidths = true;
            if (changedBandwidthHz == 250_000) MeshtasticDiscoveryUses250Khz = true;
            else MeshtasticDiscoveryUses125Khz = true;
            _isUpdatingMeshtasticDiscoveryBandwidths = false;
            return;
        }

        // Re-enabling a bandwidth after all of its slots were cleared must not
        // leave the dual-band preset in an invalid state. Select the current
        // radio slot as a safe default for the newly enabled bandwidth.
        if (changedBandwidthHz == 250_000 && MeshtasticDiscoveryUses250Khz &&
            !TryParseMeshtasticRadioChannels(MeshtasticRadioChannels250, out _, out _))
        {
            int maximum = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion)
                .GetMaximumChannel(250_000);
            MeshtasticRadioChannels250 = Math.Clamp(MeshtasticRadioChannel, 1, maximum).ToString();
        }
        else if (changedBandwidthHz == 125_000 && MeshtasticDiscoveryUses125Khz &&
                 !TryParseMeshtasticRadioChannels(MeshtasticRadioChannels125, out _, out _))
        {
            int maximum = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion)
                .GetMaximumChannel(125_000);
            MeshtasticRadioChannels125 = Math.Clamp(MeshtasticRadioChannel, 1, maximum).ToString();
        }

        SelectedMeshtasticModemPreset = (MeshtasticDiscoveryUses250Khz, MeshtasticDiscoveryUses125Khz) switch
        {
            (true, true) => MeshtasticModemPreset.AutoSf250And125,
            (true, false) => MeshtasticModemPreset.AutoSf250,
            _ => MeshtasticModemPreset.AutoSf125
        };
    }

    private void SyncMeshtasticDiscoveryBandwidths(MeshtasticModemPreset preset)
    {
        if (!MeshtasticJpLongFastProfile.IsAutoSf(preset)) return;
        _isUpdatingMeshtasticDiscoveryBandwidths = true;
        MeshtasticDiscoveryUses250Khz = preset is MeshtasticModemPreset.AutoSf250And125 or MeshtasticModemPreset.AutoSf250;
        MeshtasticDiscoveryUses125Khz = preset is MeshtasticModemPreset.AutoSf250And125 or MeshtasticModemPreset.AutoSf125;
        _isUpdatingMeshtasticDiscoveryBandwidths = false;
    }
    partial void OnSelectedMeshtasticRegionChanged(MeshtasticRegion value)
    {
        MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(SelectedMeshtasticModemPreset);
        MeshtasticRadioChannel = Math.Clamp(MeshtasticRadioChannel, 1,
            MeshtasticJpLongFastProfile.GetRegion(value).GetMaximumChannel(profile.BandwidthHz));
        OnPropertyChanged(nameof(Meshtastic250SlotRange));
        OnPropertyChanged(nameof(Meshtastic125SlotRange));
        RebuildMeshtasticSlotSelections();
        ApplyMeshtasticChannelSettings();
    }
    partial void OnSelectedMeshtasticModemPresetChanging(MeshtasticModemPreset oldValue, MeshtasticModemPreset newValue)
    {
        MeshtasticLoRaProfile newProfile = MeshtasticJpLongFastProfile.GetProfile(newValue);
        int maximumChannel = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion).GetMaximumChannel(newProfile.BandwidthHz);
        if (MeshtasticRadioChannel > maximumChannel) MeshtasticRadioChannel = maximumChannel;
    }
    partial void OnSelectedMeshtasticModemPresetChanged(MeshtasticModemPreset value)
    {
        if (!MeshtasticJpLongFastProfile.IsAutoSf(value)) _lastSpecifiedMeshtasticPreset = value;
        else SyncMeshtasticDiscoveryBandwidths(value);
        OnPropertyChanged(nameof(SelectedMeshtasticSpecifiedModemPreset));
        OnPropertyChanged(nameof(MeshtasticUses250Khz));
        OnPropertyChanged(nameof(MeshtasticUses125Khz));
        ApplyMeshtasticChannelSettings();
    }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "Meshtastic",
            async () =>
            {
                if (_hostContext is not null)
                {
                    await _hostContext.Settings.DeleteAsync();
                }
            },
            () =>
            {
                LoadMeshtasticSettings();
            });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndClearData(
            "Meshtastic",
            () =>
            {
                ClearMeshtasticMessages();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "Meshtastic",
            async () =>
            {
                if (_hostContext is not null)
                {
                    await _hostContext.Settings.DeleteAsync();
                }
            },
            () =>
            {
                LoadMeshtasticSettings();
            },
            () =>
            {
                ClearMeshtasticMessages();
            });
    }
}
