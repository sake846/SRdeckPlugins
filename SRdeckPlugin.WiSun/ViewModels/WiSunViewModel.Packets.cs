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

public sealed partial class WiSunViewModel
{
    public int PanCount => RecentPanGroups.Count(group => group.PanId.HasValue);
    public int NodeCount => RecentPanGroups.Sum(group => group.NodeCount);
    public string FrequencyHzText => $"{FrequencyMhz:F3} MHz";

    public void StepFrequencyUp() => FrequencyMhz += FrequencyStepMhz;
    public void StepFrequencyDown() => FrequencyMhz -= FrequencyStepMhz;

    public void ClearPackets()
    {
        SelectedTimelinePacket = null;
        Packets.Clear();
        RecentPanGroups.Clear();
        RecentCommunications.Clear();
        _addressResolver.Clear();
        SelectedPanGroup = null;
        LastPacket = null;
        OnPropertyChanged(nameof(PacketCount));
        OnPropertyChanged(nameof(PanCount));
        OnPropertyChanged(nameof(NodeCount));
        StatusText = "パケット履歴をクリアしました";
    }

    public async Task ExportPacketsAsync()
    {
        if (Packets.Count == 0)
        {
            MessageBox.Show("エクスポート対象のパケットデータが存在しません。", "エクスポート", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Wi-SUN 復調パケットエクスポート",
            Filter = "CSV ファイル (*.csv)|*.csv|JSON ファイル (*.json)|*.json",
            FileName = $"Wi-SUN_Packets_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            string formatId = ext == ".json" ? "json" : "csv";

            var req = new PluginExportRequest(formatId, dlg.FileName);
            var result = await _module.ExportAsync(req).ConfigureAwait(true);

            if (result.Succeeded)
            {
                MessageBox.Show(result.Message, "エクスポート完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"エクスポート失敗: {result.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public void AddPacketFrame(WiSunPacketFrame frame)
    {
        _pendingPacketFrames.Enqueue(frame);
        Interlocked.Increment(ref _pendingPacketCount);
        if (_dispatcher.CheckAccess())
        {
            FlushPendingPackets();
            return;
        }
        QueuePacketFlush();
    }

    private void DispatchToOwner(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    private void QueuePacketFlush()
    {
        if (Interlocked.Exchange(ref _packetFlushQueued, 1) != 0) return;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingPackets));
    }

    private void FlushPendingPackets()
    {
        const int maximumPacketsPerPass = 64;
        int processed = 0;
        WiSunPacketFrame? newest = null;
        while (processed < maximumPacketsPerPass && _pendingPacketFrames.TryDequeue(out WiSunPacketFrame? frame))
        {
            Interlocked.Decrement(ref _pendingPacketCount);
            _addressResolver.Observe(frame);
            Packets.Insert(0, frame);
            while (Packets.Count > 1000) Packets.RemoveAt(Packets.Count - 1);
            newest = frame;
            processed++;
        }

        if (newest is not null)
        {
            RefreshRecentPanGroups();
            if (SelectedTimelinePacket is null || !Packets.Contains(SelectedTimelinePacket))
                SelectedTimelinePacket = Packets.FirstOrDefault();
            LastPacket = newest;
            StatusText = $"最新パケット受信: {newest.Timestamp.ToLocalTime():HH:mm:ss.fff} ({newest.FrameType})";
            OnPropertyChanged(nameof(PacketCount));
            OnPropertyChanged(nameof(PanCount));
            OnPropertyChanged(nameof(NodeCount));
        }

        Interlocked.Exchange(ref _packetFlushQueued, 0);
        if (Volatile.Read(ref _pendingPacketCount) > 0) QueuePacketFlush();
    }

    private void RefreshRecentPanGroups()
    {
        bool hadSelectedPan = SelectedPanGroup is not null;
        ushort? selectedPanId = SelectedPanGroup?.PanId;
        WiSunPacketFrame? overallLatestPacket = Packets.FirstOrDefault();
        WiSunPanReceptionGroup[] groups = Packets
            .GroupBy(packet => packet.PanId)
            .Select(group => new WiSunPanReceptionGroup(group.Key, group, _addressResolver, overallLatestPacket))
            .OrderByDescending(group => group.LatestTimestamp)
            .ThenBy(group => group.PanId ?? ushort.MaxValue)
            .ToArray();

        StableRecencyOrder.Replace(
            RecentPanGroups,
            groups,
            group => group.PanIdText,
            group => group.LatestTimestamp);
        SelectedPanGroup = hadSelectedPan
            ? groups.FirstOrDefault(group => group.PanId == selectedPanId)
            : groups.FirstOrDefault();

        WiSunPacketFrame[] recentCommunications = Packets
            .Take(30)
            .Select(packet => packet with
            {
                Timestamp = packet.Timestamp.ToLocalTime(),
                SrcAddress = ResolveAddress(_addressResolver, packet.PanId, packet.SrcAddress),
                DstAddress = ResolveAddress(_addressResolver, packet.PanId, packet.DstAddress)
            })
            .ToArray();

        RecentCommunications.Clear();
        foreach (WiSunPacketFrame comm in recentCommunications) RecentCommunications.Add(comm);
    }

    private static string? ResolveAddress(WiSunAddressResolver resolver, ushort? panId, string? address) =>
        string.IsNullOrWhiteSpace(address) ? address : resolver.Resolve(panId, address);

    private async void SelectPhy(WiSunPhyProfile profile)
    {
        try
        {
            await _module.UpdateSettingsAsync(
                _module.Settings with { PhyProfile = profile });
            SynchronizeFrequency(_module.Settings.FrequencyHz);
            RebuildChannelSelections();
            OnPropertyChanged(nameof(SelectedPhyOption));
            OnPropertyChanged(nameof(IsFanSelected));
            OnPropertyChanged(nameof(IsHanSelected));
            OnPropertyChanged(nameof(IsCustomPhy));
            OnPropertyChanged(nameof(IsStandardPhy));
            OnPropertyChanged(nameof(SelectedChannelSummary));
            OnPropertyChanged(nameof(WiSunChannelRange));
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            OnPropertyChanged(nameof(SelectedPhyOption));
            OnPropertyChanged(nameof(IsFanSelected));
            OnPropertyChanged(nameof(IsHanSelected));
            OnPropertyChanged(nameof(IsCustomPhy));
            OnPropertyChanged(nameof(IsStandardPhy));
            OnPropertyChanged(nameof(WiSunChannelRange));
        }
    }

    private void RebuildChannelSelections()
    {
        _rebuildingChannelSelections = true;
        try
        {
            foreach (WiSunChannelSelectionItem item in ChannelSelections)
                item.PropertyChanged -= OnChannelSelectionChanged;
            ChannelSelections.Clear();
            WiSunPhyProfile profile = _module.Settings.PhyProfile;
            HashSet<int> selected = (profile == WiSunPhyProfile.HanBRoute
                ? _module.Settings.HanChannels
                : _module.Settings.FanChannels).ToHashSet();
            foreach (WiSunPluginModule.WiSunChannelOption option in
                     WiSunPluginModule.GetChannelOptions(profile))
            {
                var item = new WiSunChannelSelectionItem(
                    option, selected.Contains(option.Channel));
                item.PropertyChanged += OnChannelSelectionChanged;
                ChannelSelections.Add(item);
            }
        }
        finally
        {
            _rebuildingChannelSelections = false;
        }
        OnPropertyChanged(nameof(WiSunChannelRange));
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectAllChannels() => SetAllChannels(true);

    [RelayCommand]
    private void ClearChannels() => SetAllChannels(false);

    private void SetAllChannels(bool isSelected)
    {
        _rebuildingChannelSelections = true;
        try
        {
            for (int i = 0; i < ChannelSelections.Count; i++)
            {
                ChannelSelections[i].IsSelected = isSelected ? true : (i == 0);
            }
        }
        finally
        {
            _rebuildingChannelSelections = false;
        }
        _ = ApplyCurrentChannelSelectionsAsync();
    }

    private void OnChannelSelectionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_rebuildingChannelSelections ||
            args.PropertyName != nameof(WiSunChannelSelectionItem.IsSelected))
            return;
        _ = ApplyCurrentChannelSelectionsAsync(sender as WiSunChannelSelectionItem);
    }

    private async Task ApplyCurrentChannelSelectionsAsync(WiSunChannelSelectionItem? senderItem = null)
    {
        int[] selected = ChannelSelections
            .Where(item => item.IsSelected)
            .Select(item => item.Channel)
            .ToArray();
        if (selected.Length == 0)
        {
            if (senderItem != null)
            {
                _rebuildingChannelSelections = true;
                senderItem.IsSelected = true;
                _rebuildingChannelSelections = false;
                selected = [senderItem.Channel];
            }
            else if (ChannelSelections.Count > 0)
            {
                _rebuildingChannelSelections = true;
                ChannelSelections[0].IsSelected = true;
                _rebuildingChannelSelections = false;
                selected = [ChannelSelections[0].Channel];
            }
            StatusText = "受信チャンネルを1つ以上選択してください";
            return;
        }

        try
        {
            WiSunSettings updated = _module.Settings.PhyProfile == WiSunPhyProfile.HanBRoute
                ? _module.Settings with { HanChannels = selected }
                : _module.Settings with { FanChannels = selected };
            await _module.UpdateSettingsAsync(updated);
            SynchronizeFrequency(_module.Settings.FrequencyHz);
            OnPropertyChanged(nameof(SelectedChannelSummary));
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            RebuildChannelSelections();
        }
    }

    internal void SynchronizeFrequency(long frequencyHz)
    {
        _customFrequencyMHzText = null;
        _customSfdHexText = null;
        _frequencyMhz = frequencyHz / 1e6;
        _frequencyStepMhz = _module.Settings.FrequencyStepHz / 1e6;
        OnPropertyChanged(nameof(FrequencyMhz));
        OnPropertyChanged(nameof(FrequencyStepMhz));
        OnPropertyChanged(nameof(FrequencyHzText));
        OnPropertyChanged(nameof(SelectedProfileId));
        OnPropertyChanged(nameof(SelectedPhyOption));
        OnPropertyChanged(nameof(IsFanSelected));
        OnPropertyChanged(nameof(IsHanSelected));
        OnPropertyChanged(nameof(IsCustomPhy));
        OnPropertyChanged(nameof(IsStandardPhy));
        OnPropertyChanged(nameof(CustomFrequencyMHzText));
        OnPropertyChanged(nameof(SelectedBitRateOption));
        OnPropertyChanged(nameof(CustomSfdHex));
        OnPropertyChanged(nameof(EnableRawBurstLog));
        OnPropertyChanged(nameof(SelectedChannelSummary));
        RebuildChannelSelections();
    }

    private async void ApplySettings()
    {
        try
        {
            await _module.UpdateSettingsAsync(_module.Settings with
            {
                FrequencyHz = (long)Math.Round(FrequencyMhz * 1e6),
                FrequencyStepHz = (long)Math.Round(FrequencyStepMhz * 1e6),
                SquelchThresholdDbm = SquelchThresholdDbm,
                IsReceiverEnabled = IsReceiverEnabled
            });
        }
        catch (InvalidOperationException exception)
        {
            SynchronizeFrequency(_module.Settings.FrequencyHz);
            StatusText = exception.Message;
        }
    }
}
