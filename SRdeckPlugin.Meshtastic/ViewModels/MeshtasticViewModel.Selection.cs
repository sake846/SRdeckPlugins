using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    public void SelectMeshtasticNode(uint nodeNumber)
    {
        if (_nodeStore.TryGetNode(nodeNumber, out MeshtasticNodeDisplayItem? node))
            SelectedMeshtasticNode = node;
    }

    [RelayCommand]
    private void DeleteMeshtasticNode(MeshtasticNodeDisplayItem? node)
    {
        if (node is null || !MeshtasticNodes.Contains(node)) return;

        bool wasSelected = ReferenceEquals(SelectedMeshtasticNode, node);
        _nodeStore.RemoveNode(node);

        if (wasSelected) SelectedMeshtasticNode = null;
        RefreshFilteredMeshtasticNodes();
        if (wasSelected) SelectedMeshtasticNode = FilteredMeshtasticNodes.FirstOrDefault();
        RefreshMeshtasticMapMarkers();
        ScheduleMeshtasticSnapshotSave();
    }

    [RelayCommand]
    private void SelectMeshtasticMapMarker(string? markerId)
    {
        if (uint.TryParse(markerId, NumberStyles.None, CultureInfo.InvariantCulture, out uint nodeNumber))
            SelectMeshtasticNode(nodeNumber);
    }

    private void RefreshSelectedMeshtasticNodeLatestApplications()
    {
        ReplaceMeshtasticItems(SelectedMeshtasticNodeLatestApplications,
            SelectedMeshtasticNode is null ? [] : _meshtasticHistoryAnalyzer.GetLatestApplications(
                MeshtasticMessages, SelectedMeshtasticNode.NodeId));
    }

    private void RefreshMeshtasticPacketGroups()
    {
        MeshtasticHistoryAnalysisResult result = _meshtasticHistoryAnalyzer.Analyze(
            MeshtasticMessages,
            MeshtasticNodes,
            MeshtasticHistorySearchText,
            MeshtasticHistoryDecodedOnly,
            MeshtasticHistoryDirectOnly,
            MeshtasticHistoryDisplayLimit);
        ApplyMeshtasticHistoryAnalysis(result);
    }

    private void ApplyMeshtasticHistoryAnalysis(MeshtasticHistoryAnalysisResult result)
    {
        // WPF may clear selection while a row is replaced or a filter resets the view.
        MeshtasticDisplayItem? selected = SelectedTimelineMessage;
        ReplaceMeshtasticItems(VisibleMeshtasticMessages, result.FilteredMessages);
        ReplaceMeshtasticItems(MeshtasticPacketGroups, result.PacketGroups, static (left, right) =>
            left.Sender == right.Sender && left.SenderName == right.SenderName &&
            left.PacketIdText == right.PacketIdText && left.Port == right.Port &&
            left.Summary == right.Summary && left.Receptions.SequenceEqual(right.Receptions));
        ReplaceMeshtasticItems(MeshtasticReceptionAggregates, result.ReceptionAggregates);
        ReplaceMeshtasticItems(MeshtasticTextMessages, result.TextMessages);

        MeshtasticActiveNodeCount = result.ActiveNodeCount;
        MeshtasticUndecodedHistoryCount = result.UndecodedHistoryCount;

        SelectedTimelineMessage = selected is null ? VisibleMeshtasticMessages.FirstOrDefault()
            : VisibleMeshtasticMessages.FirstOrDefault(item => item.Sender == selected.Sender &&
                item.PacketId == selected.PacketId && item.ReceivedAt == selected.ReceivedAt &&
                item.Radio == selected.Radio && item.PayloadHex == selected.PayloadHex)
                ?? VisibleMeshtasticMessages.FirstOrDefault();
    }

    private void ScheduleMeshtasticDerivedRefresh()
    {
        _historyVersion++;
        _meshtasticDerivedRefreshTimer ??= CreateMeshtasticTimer(
            TimeSpan.FromMilliseconds(250),
            () =>
            {
                _ = RefreshMeshtasticHistoryInBackground();
                RefreshSelectedMeshtasticNodeLatestApplications();
                RefreshFilteredMeshtasticNodes();
            });
        if (!_meshtasticDerivedRefreshTimer.IsEnabled) _meshtasticDerivedRefreshTimer.Start();
    }

    private void ScheduleMeshtasticSnapshotSave()
    {
        _meshtasticSnapshotSaveTimer ??= CreateMeshtasticTimer(TimeSpan.FromSeconds(2), SaveMeshtasticState);
        if (!_meshtasticSnapshotSaveTimer.IsEnabled) _meshtasticSnapshotSaveTimer.Start();
    }

    private static DispatcherTimer CreateMeshtasticTimer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private void FlushMeshtasticPendingWork()
    {
        if (_meshtasticDerivedRefreshTimer?.IsEnabled == true)
        {
            _meshtasticDerivedRefreshTimer.Stop();
            RefreshMeshtasticPacketGroups();
            RefreshSelectedMeshtasticNodeLatestApplications();
            RefreshFilteredMeshtasticNodes();
        }
        if (_meshtasticSnapshotSaveTimer?.IsEnabled == true)
        {
            _meshtasticSnapshotSaveTimer.Stop();
            SaveMeshtasticState();
        }
        ApplyPendingMeshtasticDiagnostics();
    }

    private static string ResolveMeshtasticPresetName(MeshtasticRadioReception radio)
    {
        MeshtasticLoRaProfile? profile = MeshtasticJpLongFastProfile
            .GetDetectionProfiles(MeshtasticModemPreset.AutoSf250And125)
            .FirstOrDefault(candidate => candidate.BandwidthHz == radio.BandwidthHz &&
                                         candidate.SpreadingFactor == radio.SpreadingFactor);
        return profile?.Name ?? $"SF{radio.SpreadingFactor}/{radio.BandwidthHz / 1000.0:0.###}k";
    }

    private void RefreshFilteredMeshtasticNodes()
    {
        IEnumerable<MeshtasticNodeDisplayItem> filtered = MeshtasticNodes;
        string search = MeshtasticNodeSearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(node =>
                node.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                node.NodeId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                node.Identity.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (MeshtasticNodesActiveOnly)
            filtered = filtered.Where(node => node.ActivityStatus is "新規" or "活動中");
        if (MeshtasticNodesDirectOnly)
            filtered = filtered.Where(node => node.DirectReceptionCount > 0);
        if (MeshtasticNodesWithPositionOnly)
            filtered = filtered.Where(node => node.HasPosition);

        StableRecencyOrder.Replace(
            FilteredMeshtasticNodes,
            filtered,
            node => node.NodeNumber,
            node => node.LastSeenAt ?? DateTimeOffset.MinValue);
        if (SelectedMeshtasticNode is not null && !FilteredMeshtasticNodes.Contains(SelectedMeshtasticNode))
            SelectedMeshtasticNode = FilteredMeshtasticNodes.FirstOrDefault();
    }

    private void RefreshMeshtasticMapMarkers()
    {
        if (_restoringMeshtasticState) return;
        IEnumerable<MeshtasticMapPoint> points = MeshtasticMapPoints;
        if (MeshtasticMapActiveOnly)
            points = points.Where(point => point.ActivityStatus is "新規" or "活動中");
        if (MeshtasticMapDirectOnly)
            points = points.Where(point => point.HasDirectReception);

        var markers = new List<GeoMapMarker>();
        foreach (MeshtasticMapPoint point in points)
        {
            string reception = point.HasDirectReception ? "直接受信あり" : "中継受信のみ";
            markers.Add(new GeoMapMarker(
                point.NodeNumber.ToString(CultureInfo.InvariantCulture),
                point.Latitude,
                point.Longitude,
                point.Label,
                $"{point.ActivityStatus} / {reception}\n{point.Coordinates}",
                MeshtasticMapPresentation.GetNodeColor(point.ActivityStatus),
                Symbol: "station",
                IsSelected: point.IsSelected));
        }
        ReplaceMeshtasticItems(MapMarkers, markers);
    }

    [ObservableProperty] private long _meshtasticPreambleCount;
    [ObservableProperty] private long _meshtasticSynchronizedCount;
    [ObservableProperty] private long _meshtasticHeaderCount;
    [ObservableProperty] private long _meshtasticPayloadCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeshtasticDecodeRateText))]
    private long _meshtasticPacketCount;
    [ObservableProperty] private long _meshtasticDuplicateCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeshtasticDecodeRateText))]
    private long _meshtasticDataCount;
    [ObservableProperty] private long _meshtasticDroppedBlockCount;
    [ObservableProperty] private int _meshtasticQueueDepth;
    [ObservableProperty] private int _meshtasticMaximumQueueDepth;
    [ObservableProperty] private double _meshtasticCurrentQueueDelayMs;
    [ObservableProperty] private double _meshtasticAverageQueueDelayMs;
    [ObservableProperty] private double _meshtasticMaximumQueueDelayMs;
    [ObservableProperty] private double _meshtasticCurrentProcessingTimeMs;
    [ObservableProperty] private double _meshtasticAverageProcessingTimeMs;
    [ObservableProperty] private double _meshtasticMaximumProcessingTimeMs;
    [ObservableProperty] private double _meshtasticAverageChannelizationCpuMs;
    [ObservableProperty] private double _meshtasticAverageDetectionCpuMs;
    [ObservableProperty] private double _meshtasticCurrentInputBlockTimeMs;
    [ObservableProperty] private double _meshtasticCurrentProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticAverageProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticMaximumProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticOldestDeferredIqMs;
    [ObservableProperty] private double _meshtasticDeferredRetentionRemainingMs;
    [ObservableProperty] private long _meshtasticDeferredRecoveredBlocks;
    [ObservableProperty] private long _meshtasticExpiredHistoryBlocks;
    [ObservableProperty] private string _meshtasticPerformanceStatus = "正常";
    [ObservableProperty] private string _meshtasticPerformanceDetails = "処理負荷を計測中です。";

    public string MeshtasticDecodeRateText => MeshtasticPacketCount == 0
        ? "—"
        : $"{MeshtasticDataCount * 100.0 / MeshtasticPacketCount:F1} %";
}
