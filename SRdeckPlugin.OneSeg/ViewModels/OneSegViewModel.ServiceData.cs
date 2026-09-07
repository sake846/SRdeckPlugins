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

internal sealed partial class OneSegViewModel
{
    public void UpdateActiveStationQuality(double merDb, long tsPackets, long rsPackets, long rsRejected)
    {
        if (IsScanning || selectedStation is null) return;
        if (selectedStation.ChannelNumber != SelectedChannel.Number) return;
        int index = Stations.IndexOf(selectedStation);
        if (index < 0) return;
        OneSegStation current = selectedStation;
        if (current.MerDb == merDb && current.TransportPackets == tsPackets &&
            current.ReedSolomonPackets == rsPackets && current.ReedSolomonRejected == rsRejected)
        {
            return;
        }
        OneSegStation updated = current.UpdateInfo(
            merDb: merDb,
            transportPackets: tsPackets,
            reedSolomonPackets: rsPackets,
            reedSolomonRejected: rsRejected);
        Stations[index] = updated;
        selectedStation = updated;
        OnPropertyChanged(nameof(SelectedStation));
    }

    public void UpdateSelectedStationSilently(OneSegStation updated)
    {
        selectedStation = updated;
        OnPropertyChanged(nameof(SelectedStation));
    }

    public void SetPrograms(IReadOnlyList<OneSegProgramInfo> values)
    {
        Programs.Clear();
        foreach (OneSegProgramInfo value in values) Programs.Add(value);
    }

    public void SetConditionalAccess(IReadOnlyList<OneSegConditionalAccessInfo> values)
    {
        ConditionalAccess.Clear();
        foreach (OneSegConditionalAccessInfo value in values) ConditionalAccess.Add(value);
    }

    public void SetNetwork(OneSegNetworkInfo value)
    {
        OneSegNetworkInfo? old = Networks.FirstOrDefault(item => item.NetworkId == value.NetworkId);
        if (old is not null) Networks.Remove(old);
        Networks.Add(value);
        NetworkStatus = $"ネットワーク: {value.DisplayName} / TS {value.Transports.Count}件";
    }

    public void SetBroadcastClock(OneSegBroadcastClock value)
    {
        BroadcastClockStatus = value.DisplayText;
        broadcastOffset = value.LocalTime - DateTimeOffset.Now;
        RefreshProgramSummary(DateTimeOffset.Now);
    }

    public void SetBroadcaster(OneSegBroadcasterInfo value)
    {
        OneSegBroadcasterInfo? old = Broadcasters.FirstOrDefault(item =>
            item.OriginalNetworkId == value.OriginalNetworkId);
        if (old is not null) Broadcasters.Remove(old);
        Broadcasters.Add(value);
    }

    public void SetTransportClock(OneSegTransportClock value) => PcrStatus = value.DisplayText;

    public void SetDataModule(OneSegDataModule value)
    {
        OneSegDataModule? old = DataModules.FirstOrDefault(item => item.Pid == value.Pid &&
            item.DownloadId == value.DownloadId && item.ModuleId == value.ModuleId && item.Version == value.Version);
        if (old is not null && ModulesEquivalent(old, value)) return;
        if (old is not null) DataModules.Remove(old);
        DataModules.Add(value);
        while (DataModules.Count > 256) DataModules.RemoveAt(0);
        DataBroadcastStatus = $"データ放送: {DataModules.Count}モジュール / 完了 {DataModules.Count(item => item.Complete)}";
        RebuildBmlResources();
    }

    internal bool TryGetBmlResource(string path, out OneSegBmlResource? resource)
    {
        string normalized = SRdeckPlugin.OneSeg.Transport.BmlResourceParser.NormalizePath(path);
        if (bmlResources.TryGetValue(normalized, out resource)) return true;
        resource = bmlResources.Values.FirstOrDefault(value =>
            normalized.EndsWith(value.Path, StringComparison.OrdinalIgnoreCase) ||
            value.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(value.Path), Path.GetFileName(normalized),
                StringComparison.OrdinalIgnoreCase));
        return resource is not null;
    }

    internal void ReportBmlViewerStatus(string value) => BmlViewerStatus = value;

    public void ResetServiceData()
    {
        Programs.Clear();
        Events.Clear();
        ConditionalAccess.Clear();
        Networks.Clear();
        Broadcasters.Clear();
        DataModules.Clear();
        bmlResources = new Dictionary<string, OneSegBmlResource>(StringComparer.OrdinalIgnoreCase);
        BmlEntryPath = "";
        BmlViewerStatus = "BML: 文書を受信待ち";
        BmlRevision++;
        Caption = "";
        broadcastOffset = null;
        BroadcastClockStatus = "放送時刻: 受信待ち";
        PcrStatus = "PCR: 受信待ち";
        NetworkStatus = "ネットワーク情報: 受信待ち";
        DataBroadcastStatus = "データ放送: 受信待ち";
        OfdmLockStatus = "未同期";
        TmccLockStatus = "未同期";
        MerStatus = "—";
        RsQualityStatus = "—";
        PacketStatus = "TS 0 / RS 0 / RS棄却 0 / CC誤り 0";
    }

    private void RebuildBmlResources()
    {
        var resources = new Dictionary<string, OneSegBmlResource>(StringComparer.OrdinalIgnoreCase);
        OneSegDataModule[] completeModules = DataModules.Where(module => module.Complete).ToArray();
        int resourceCount = 0;
        foreach (OneSegDataModule module in completeModules)
        {
            foreach (OneSegBmlResource resource in module.Resources ?? [])
            {
                resourceCount++;
                string path = SRdeckPlugin.OneSeg.Transport.BmlResourceParser.NormalizePath(resource.Path);
                resources.TryAdd(path, resource);
                if (module.ComponentTag is byte tag)
                    resources[$"{tag:X2}/{module.ModuleId:X4}/{path}"] = resource;
            }
        }
        bmlResources = resources;
        OneSegBmlResource? entry = resources.Values
            .OrderByDescending(IsBmlDocument)
            .ThenByDescending(resource => resource.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(resource => Path.GetExtension(resource.Path).Equals(".bml", StringComparison.OrdinalIgnoreCase))
            .ThenBy(resource => resource.Path.Length)
            .FirstOrDefault(resource => !resource.ContentType.Contains("resourcelist", StringComparison.OrdinalIgnoreCase) &&
                (resource.ContentType.Contains("bml", StringComparison.OrdinalIgnoreCase) ||
                resource.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(resource.Path).ToLowerInvariant() is ".bml" or ".xml" or ".html" or ".htm"));
        BmlEntryPath = entry?.Path ?? "";
        BmlViewerStatus = entry is null
            ? $"BML: リソース {resourceCount}件 / 文書を受信待ち"
            : $"BML: {entry.Path} / リソース {resourceCount}件";
        BmlRevision++;
    }

    private static bool IsBmlDocument(OneSegBmlResource resource) =>
        !resource.ContentType.Contains("resourcelist", StringComparison.OrdinalIgnoreCase) &&
        (resource.ContentType.Equals("application/bml+xml", StringComparison.OrdinalIgnoreCase) ||
         resource.ContentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
         resource.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase));

    private static bool ModulesEquivalent(OneSegDataModule left, OneSegDataModule right)
    {
        if (left.Pid != right.Pid || left.DownloadId != right.DownloadId ||
            left.ModuleId != right.ModuleId || left.Version != right.Version ||
            left.Size != right.Size || left.Complete != right.Complete ||
            left.Preview != right.Preview || left.ResourceCount != right.ResourceCount ||
            left.ComponentTag != right.ComponentTag) return false;
        IReadOnlyList<OneSegBmlResource> leftResources = left.Resources ?? [];
        IReadOnlyList<OneSegBmlResource> rightResources = right.Resources ?? [];
        return leftResources.Zip(rightResources).All(pair =>
            pair.First.Path == pair.Second.Path && pair.First.ContentType == pair.Second.ContentType &&
            pair.First.Content.AsSpan().SequenceEqual(pair.Second.Content));
    }

    public void SetEvent(OneSegEventInfo value)
    {
        OneSegEventInfo? existing = Events.FirstOrDefault(item => item.ServiceId == value.ServiceId && item.EventId == value.EventId);
        if (existing is not null) Events.Remove(existing);
        Events.Add(value);
        SortEvents();
        while (Events.Count > 256) Events.RemoveAt(Events.Count - 1);
        RefreshProgramSummary(DateTimeOffset.Now);
    }

    public void RefreshProgramSummary(DateTimeOffset now)
    {
        if (broadcastOffset is TimeSpan offset && Math.Abs(offset.TotalMinutes) < 10)
            now += offset;
        ushort? serviceId = SelectedStation?.ServiceId;
        IEnumerable<OneSegEventInfo> candidates = Events.Where(item =>
            !serviceId.HasValue || item.ServiceId == serviceId.Value);
        OneSegEventInfo? current = candidates.FirstOrDefault(item =>
            item.StartTime is DateTimeOffset start && item.EndTime is DateTimeOffset end &&
            start <= now && now < end);
        OneSegEventInfo? next = candidates.Where(item => item.StartTime > now)
            .OrderBy(item => item.StartTime).FirstOrDefault();

        CurrentProgramTitle = current is null || string.IsNullOrWhiteSpace(current.Title)
            ? "番組情報を受信待ち" : current.Title;
        CurrentProgramTime = current?.StartTime is DateTimeOffset currentStart &&
            current.EndTime is DateTimeOffset currentEnd
            ? $"{currentStart:HH:mm} ～ {currentEnd:HH:mm}" : "--:-- ～ --:--";
        NextProgramText = next is null ? "次番組: --" :
            $"次番組 {next.StartTime:HH:mm}: {(string.IsNullOrWhiteSpace(next.Title) ? "番組名未取得" : next.Title)}";
        ProgramProgress = current?.StartTime is DateTimeOffset startTime &&
            current.EndTime is DateTimeOffset endTime && endTime > startTime
            ? Math.Clamp((now - startTime).TotalMilliseconds /
                (endTime - startTime).TotalMilliseconds * 100, 0, 100) : 0;
    }

    public void SetEmergency(bool active)
    {
        EmergencyActive = active;
        EmergencyStatus = active ? "警告: 緊急警報放送を受信中" : "緊急警報: 待機中";
    }

    public void SetAudioTracks(IReadOnlyList<OneSegAudioTrackOption> values, int preferredTrackId)
    {
        if (AudioTracks.Select(item => (item.Id, item.Name)).SequenceEqual(
            values.Select(item => (item.Id, item.Name)))) return;
        synchronizingSelection = true;
        try
        {
            AudioTracks.Clear();
            foreach (OneSegAudioTrackOption value in values) AudioTracks.Add(value);
            int requested = preferredTrackId >= 0 ? preferredTrackId : desiredAudioTrackId;
            selectedAudioTrack = AudioTracks.FirstOrDefault(item => item.Id == requested) ?? AudioTracks.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedAudioTrack));
        }
        finally { synchronizingSelection = false; }
    }

    private void SelectRelative(int delta)
    {
        if (Stations.Count == 0) return;
        int index = SelectedStation is null ? 0 : Stations.IndexOf(SelectedStation);
        if (index < 0) index = 0;
        SelectedStation = Stations[(index + delta + Stations.Count) % Stations.Count];
    }

    private void ReplaceStations(IEnumerable<OneSegStation> values)
    {
        Stations.Clear();
        foreach (OneSegStation station in OrderStations(values))
            Stations.Add(station);
    }

    private void SortStations()
    {
        OneSegStation[] sorted = OrderStations(Stations).ToArray();
        if (Stations.SequenceEqual(sorted)) return;
        Stations.Clear();
        foreach (OneSegStation station in sorted) Stations.Add(station);
    }

    private static IOrderedEnumerable<OneSegStation> OrderStations(IEnumerable<OneSegStation> values) =>
        values.OrderBy(item => item.RemoteControlKeyId ?? byte.MaxValue)
            .ThenBy(item => item.ChannelNumber)
            .ThenBy(item => item.ServiceId)
            .ThenBy(item => item.ServiceName);

    private void SortEvents()
    {
        OneSegEventInfo[] sorted = Events.OrderBy(item => item.StartTime is null)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.EventId)
            .ToArray();
        if (Events.SequenceEqual(sorted)) return;
        Events.Clear();
        foreach (OneSegEventInfo value in sorted) Events.Add(value);
    }

    private void PublishCaptionSettings() => CaptionSettingsChanged?.Invoke(
        CaptionsEnabled, CaptionFontSize, CaptionBackgroundEnabled, SelectedCaptionPosition.Id);

    private void NotifyStationCommands()
    {
        PreviousStationCommand.NotifyCanExecuteChanged();
        NextStationCommand.NotifyCanExecuteChanged();
    }
}
