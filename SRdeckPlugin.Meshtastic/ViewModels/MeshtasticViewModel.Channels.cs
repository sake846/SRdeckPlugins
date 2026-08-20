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
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private void ApplyMeshtasticChannelSettings()
    {
        if (_isLoadingMeshtasticSettings) return;
        // Slot highlighting follows the checkbox state even when the current
        // selection is temporarily invalid (for example, after clearing all).
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(SelectedMeshtasticModemPreset);
        if (!TryGetMeshtasticBandwidthSlots(out List<MeshtasticBandwidthSlots> bandwidthSlots, out string parseError))
        {
            MeshtasticChannelSettingStatus = parseError;
            return;
        }
        if (!_meshtasticReceiveService.TryConfigureChannels(
                SelectedMeshtasticRegion,
                SelectedMeshtasticModemPreset,
                bandwidthSlots,
                out string error))
        {
            MeshtasticChannelSettingStatus = error;
            return;
        }

        SaveMeshtasticSettings();
        MeshtasticRegionProfile region = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion);
        (int FrequencyHz, int BandwidthHz)[] targetBands = bandwidthSlots
            .SelectMany(selection => selection.RadioChannels.Select(channel =>
                (region.CalculateChannelFrequencyHz(channel, selection.BandwidthHz), selection.BandwidthHz)))
            .ToArray();
        MeshtasticRadioChannel = bandwidthSlots[0].RadioChannels[0];
        string slotText = FormatMeshtasticBandwidthSlots(bandwidthSlots);
        int detectorCount = bandwidthSlots.Sum(selection => selection.RadioChannels.Count *
            MeshtasticJpLongFastProfile.GetDetectionProfiles(SelectedMeshtasticModemPreset).Count(candidate => candidate.BandwidthHz == selection.BandwidthHz));
        MeshtasticChannelSettingStatus = $"設定を適用しました: {profile.Name} / {slotText}（{detectorCount}検出器）";
        SetMeshtasticReceiverStatus(
            $"受信 {MeshtasticMessages.Count}件 / Node {MeshtasticNodes.Count}",
            OverallStatusKind.Running);
        ApplyMeshtasticRadioTuning(targetBands);
        ResetMeshtasticStatistics();
    }

    private bool TryGetMeshtasticBandwidthSlots(out List<MeshtasticBandwidthSlots> selections, out string error)
    {
        selections = [];
        foreach (MeshtasticLoRaProfile profile in MeshtasticJpLongFastProfile.GetChannelProfiles(SelectedMeshtasticModemPreset))
        {
            string text = profile.BandwidthHz == 125_000 ? MeshtasticRadioChannels125 : MeshtasticRadioChannels250;
            if (!TryParseMeshtasticRadioChannels(text, out int[] slots, out error))
            {
                error = $"{profile.BandwidthHz / 1000} kHz: {error}";
                return false;
            }
            selections.Add(new MeshtasticBandwidthSlots(profile.BandwidthHz, slots));
        }
        error = string.Empty;
        return true;
    }

    private static string FormatMeshtasticBandwidthSlots(IEnumerable<MeshtasticBandwidthSlots> selections) =>
        string.Join(" / ", selections.Select(value => $"{value.BandwidthHz / 1000}kHz slot {string.Join(',', value.RadioChannels)}"));

    private void RebuildMeshtasticSlotSelections()
    {
        _isUpdatingMeshtasticSlotSelection = true;
        try
        {
            RebuildMeshtasticSlotSelections(MeshtasticSlots250, 250_000, MeshtasticRadioChannels250);
            RebuildMeshtasticSlotSelections(MeshtasticSlots125, 125_000, MeshtasticRadioChannels125);
            MeshtasticRadioChannels250 = FormatSelectedMeshtasticSlots(MeshtasticSlots250);
            MeshtasticRadioChannels125 = FormatSelectedMeshtasticSlots(MeshtasticSlots125);
        }
        finally
        {
            _isUpdatingMeshtasticSlotSelection = false;
        }
    }

    private void RebuildMeshtasticSlotSelections(ObservableCollection<MeshtasticSlotSelectionItem> target,
        int bandwidthHz, string savedSelection)
    {
        int maximum = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion).GetMaximumChannel(bandwidthHz);
        TryParseMeshtasticRadioChannels(savedSelection, out int[] parsed, out _);
        HashSet<int> selected = parsed.Where(slot => slot <= maximum).ToHashSet();
        if (selected.Count == 0)
            selected.Add(Math.Clamp(MeshtasticRadioChannel, 1, maximum));

        target.Clear();
        for (int slot = 1; slot <= maximum; slot++)
        {
            var item = new MeshtasticSlotSelectionItem(slot, selected.Contains(slot));
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MeshtasticSlotSelectionItem.IsSelected))
                    OnMeshtasticSlotSelectionChanged(target, bandwidthHz);
            };
            target.Add(item);
        }
    }

    private static void SyncMeshtasticSlotSelections(ObservableCollection<MeshtasticSlotSelectionItem> target, string selection)
    {
        if (!TryParseMeshtasticRadioChannels(selection, out int[] parsed, out _)) return;
        HashSet<int> selected = parsed.ToHashSet();
        foreach (MeshtasticSlotSelectionItem item in target)
            item.IsSelected = selected.Contains(item.Slot);
    }

    private void OnMeshtasticSlotSelectionChanged(ObservableCollection<MeshtasticSlotSelectionItem> source, int bandwidthHz)
    {
        if (_isUpdatingMeshtasticSlotSelection) return;
        _isUpdatingMeshtasticSlotSelection = true;
        try
        {
            string value = FormatSelectedMeshtasticSlots(source);
            if (bandwidthHz == 250_000) MeshtasticRadioChannels250 = value;
            else MeshtasticRadioChannels125 = value;
        }
        finally
        {
            _isUpdatingMeshtasticSlotSelection = false;
        }
        ApplyMeshtasticChannelSettings();
    }

    private static string FormatSelectedMeshtasticSlots(IEnumerable<MeshtasticSlotSelectionItem> source) =>
        string.Join(',', source.Where(item => item.IsSelected).Select(item => item.Slot));

    [RelayCommand]
    private void SelectAllMeshtasticSlots250() => SetAllMeshtasticSlots(MeshtasticSlots250, 250_000, true);

    [RelayCommand]
    private void ClearMeshtasticSlots250() => SetAllMeshtasticSlots(MeshtasticSlots250, 250_000, false);

    [RelayCommand]
    private void SelectAllMeshtasticSlots125() => SetAllMeshtasticSlots(MeshtasticSlots125, 125_000, true);

    [RelayCommand]
    private void ClearMeshtasticSlots125() => SetAllMeshtasticSlots(MeshtasticSlots125, 125_000, false);

    private void SetAllMeshtasticSlots(ObservableCollection<MeshtasticSlotSelectionItem> source,
        int bandwidthHz, bool isSelected)
    {
        _isUpdatingMeshtasticSlotSelection = true;
        try
        {
            foreach (MeshtasticSlotSelectionItem item in source) item.IsSelected = isSelected;
        }
        finally
        {
            _isUpdatingMeshtasticSlotSelection = false;
        }
        OnMeshtasticSlotSelectionChanged(source, bandwidthHz);
    }

    private static bool TryParseMeshtasticRadioChannels(string text, out int[] channels, out string error)
    {
        var values = new SortedSet<int>();
        foreach (string part in (text ?? string.Empty).Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int channel) || channel < 1)
            {
                channels = [];
                error = "周波数スロットはカンマ区切りの正の整数で入力してください（例: 10,11,12）。";
                return false;
            }
            values.Add(channel);
        }
        channels = values.ToArray();
        error = channels.Length == 0 ? "周波数スロットを1つ以上入力してください。" : string.Empty;
        return channels.Length > 0;
    }
}
