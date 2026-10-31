using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using SRdeckPlugin.Aprs.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Aprs.ViewModels;

public sealed record AprsStationRow(string Key, string Source, string Label, DateTimeOffset LastReceived,
    int Packets, double? Latitude, double? Longitude, string Kind, string Information, bool IsAlive);

public sealed class AprsViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, AprsStationRow> stations = new(StringComparer.Ordinal);
    private string status = "待機中", diagnostics = "入力待ち", frequencyText = "144660000", operationStatus = "";
    private AprsPacket? selectedPacket;
    private string? selectedGraphStation;
    private AprsMeasurement? selectedGraphMetric;
    public ObservableCollection<AprsPacket> Packets { get; } = [];
    public ObservableCollection<AprsStationRow> Stations { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];
    public ObservableCollection<string> GraphStations { get; } = [];
    public ObservableCollection<AprsMeasurement> GraphMetrics { get; } = [];
    public IReadOnlyList<AprsGraphPoint> GraphPoints { get; private set; } = [];
    public string? SelectedGraphStation
    {
        get => selectedGraphStation;
        set { if (selectedGraphStation == value) return; selectedGraphStation = value; Changed(); RefreshGraphMetrics(); }
    }
    public AprsMeasurement? SelectedGraphMetric
    {
        get => selectedGraphMetric;
        set { if (selectedGraphMetric == value) return; selectedGraphMetric = value; Changed(); RefreshGraphPoints(); }
    }
    public string GraphStatus => GraphPoints.Count == 0
        ? "数値データは未受信です。局・対象と項目を選ぶと、保持中の受信履歴を表示します。"
        : $"{SelectedGraphStation} / {SelectedGraphMetric?.Label} / {GraphPoints.Count:N0}点。横軸は受信時刻（端末の現地時刻）です。欠測値は描画しません。";
    public IPluginRuntimeDiagnostics RuntimeDiagnostics { get; set; } = NullPluginRuntimeDiagnostics.Instance;
    public Func<long, CancellationToken, ValueTask<PluginTuningResult>>? ApplyFrequency { get; set; }
    public Func<PluginExportRequest, CancellationToken, ValueTask<PluginExportResult>>? Export { get; set; }
    public string Status { get => status; set { status = value; Changed(); Changed(nameof(OverallStatusKind)); } }
    public OverallStatusKind OverallStatusKind => status.StartsWith("監視中", StringComparison.Ordinal)
        ? OverallStatusKind.Running : OverallStatusKind.Idle;
    public string Diagnostics { get => diagnostics; set { diagnostics = value; Changed(); } }
    public string FrequencyText { get => frequencyText; set { frequencyText = value; Changed(); } }
    public string OperationStatus { get => operationStatus; set { operationStatus = value; Changed(); } }
    public AprsPacket? SelectedPacket { get => selectedPacket; set { selectedPacket = value; Changed(); Changed(nameof(SelectedDetails)); } }
    public string SelectedDetails => SelectedPacket is { } p
        ? $"{p.ReceivedAt:O}\n{p.MonitorText}\n種別 {p.Kind}\n宛先 {p.Addressee ?? "—"} / メッセージID {p.MessageId ?? "—"}\n{p.Message}\n" +
          (p.Telemetry is { } telemetry ? $"テレメトリ連番 {telemetry.Sequence?.ToString() ?? "—"}\n" : "") +
          string.Join('\n', AprsMeasurements.From(p).Select(v => $"{v.Label}: {v.Value:G8}"))
        : "パケットを選択すると詳細を表示します。";
    public string Overview => $"保持中 {Packets.Count:N0} パケット / {Stations.Count:N0} 局・対象\n位置あり {MapMarkers.Count:N0} 対象\n" +
        (Packets.Count == 0 ? "まだAPRSを受信していません。周波数とアンテナを確認して監視を続けてください。" : $"直近 {Packets[0].Source} / {Packets[0].Kind}");
    public string MapStatus => MapMarkers.Count == 0
        ? "位置情報は未受信です。位置を解析できた局・オブジェクト・アイテムを地図に表示します。"
        : "受信した局・オブジェクト・アイテムの最新位置です。中継局の位置を示すものではありません。";
    public ICommand ApplyFrequencyCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand ExportCommand { get; }

    public AprsViewModel()
    {
        ApplyFrequencyCommand = new AsyncCommand(_ => TuneAsync());
        PresetCommand = new AsyncCommand(async value => { FrequencyText = value?.ToString() ?? ""; await TuneAsync(); });
        ExportCommand = new AsyncCommand(async _ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "APRS受信履歴を保存", FileName = "aprs-history", AddExtension = true,
                Filter = "JSON (*.json)|*.json|CSV (*.csv)|*.csv|TNC2 (*.txt)|*.txt"
            };
            if (dialog.ShowDialog() != true || Export is null) return;
            string format = dialog.FilterIndex switch { 2 => "csv", 3 => "tnc2", _ => "json" };
            OperationStatus = (await Export(new(format, dialog.FileName), CancellationToken.None)).Message;
        }, e => OperationStatus = $"操作を完了できませんでした（{e.GetType().Name}）。");
    }

    private async Task TuneAsync()
    {
        if (!long.TryParse(FrequencyText, NumberStyles.None, CultureInfo.InvariantCulture, out long frequency) ||
            frequency is < 1_000_000 or > 2_147_483_647)
        { OperationStatus = "周波数を整数Hzで入力してください。"; return; }
        if (ApplyFrequency is null) return;
        try { OperationStatus = (await ApplyFrequency(frequency, CancellationToken.None)).Message; }
        catch (Exception e) when (e is not OperationCanceledException)
        { OperationStatus = $"設定できませんでした（{e.GetType().Name}）。"; }
    }

    public void AddPackets(IReadOnlyList<AprsPacket> packets, int maximum)
    {
        if (packets.Count == 0) return;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (AprsPacket packet in packets)
        {
            Packets.Insert(0, packet);
            stations.TryGetValue(packet.EntityKey, out var previous);
            var row = new AprsStationRow(packet.EntityKey, packet.Source, packet.Label, packet.ReceivedAt,
                (previous?.Packets ?? 0) + 1, packet.Latitude ?? previous?.Latitude,
                packet.Longitude ?? previous?.Longitude, packet.Kind, packet.Information, packet.IsAlive);
            stations[row.Key] = row;
            touched.Add(row.Key);
        }
        while (Packets.Count > maximum) Packets.RemoveAt(Packets.Count - 1);
        // Bound the independent current-state projection as well as the event log.
        foreach (var expired in stations.Values.OrderByDescending(s => s.LastReceived).Skip(maximum).ToArray())
        { stations.Remove(expired.Key); touched.Add(expired.Key); }
        foreach (string key in touched)
        {
            var old = Stations.FirstOrDefault(s => s.Key == key);
            if (old is not null) Stations.Remove(old);
            var marker = MapMarkers.FirstOrDefault(m => m.Id == key);
            if (marker is not null) MapMarkers.Remove(marker);
            if (!stations.TryGetValue(key, out var row)) continue;
            Stations.Insert(0, row);
            if (row.IsAlive && row.Latitude is >= -90 and <= 90 && row.Longitude is >= -180 and <= 180)
                MapMarkers.Add(new(row.Key, row.Latitude.Value, row.Longitude.Value, row.Label,
                    WebUtility.HtmlEncode($"送信元 {row.Source}\n{row.LastReceived:O}\n{row.Information}")));
        }
        Changed(nameof(Overview)); Changed(nameof(MapStatus));
        string[] available = Packets.Where(p => AprsMeasurements.From(p).Count > 0)
            .Select(p => p.EntityKey).Distinct().Order(StringComparer.Ordinal).ToArray();
        string? stationKey = selectedGraphStation;
        AprsMeasurement? metricSelection = selectedGraphMetric;
        if (!GraphStations.SequenceEqual(available))
        {
            GraphStations.Clear();
            foreach (string station in available) GraphStations.Add(station);
        }
        selectedGraphStation = available.Contains(stationKey, StringComparer.Ordinal) ? stationKey : available.FirstOrDefault();
        Changed(nameof(SelectedGraphStation));
        selectedGraphMetric = metricSelection;
        RefreshGraphMetrics();
    }

    private void RefreshGraphMetrics()
    {
        AprsMeasurement[] available = Packets.Where(p => p.EntityKey == SelectedGraphStation)
            .SelectMany(AprsMeasurements.From).DistinctBy(v => v.Key).ToArray();
        string? key = selectedGraphMetric?.Key;
        // Keep selector instances stable while new samples arrive.
        if (!GraphMetrics.Select(v => v.Key).SequenceEqual(available.Select(v => v.Key)))
        {
            GraphMetrics.Clear();
            foreach (var metric in available) GraphMetrics.Add(metric);
        }
        selectedGraphMetric = GraphMetrics.FirstOrDefault(v => v.Key == key)
            ?? GraphMetrics.FirstOrDefault(v => v.Key == "temperature")
            ?? GraphMetrics.FirstOrDefault(v => v.Key == "analog1") ?? GraphMetrics.FirstOrDefault();
        Changed(nameof(SelectedGraphMetric));
        RefreshGraphPoints();
    }

    private void RefreshGraphPoints()
    {
        GraphPoints = Packets.Where(p => p.EntityKey == SelectedGraphStation)
            .Select(p => (Packet: p, Metric: AprsMeasurements.From(p).FirstOrDefault(v => v.Key == SelectedGraphMetric?.Key)))
            .Where(v => v.Metric is not null).OrderBy(v => v.Packet.ReceivedAt)
            .Select(v => new AprsGraphPoint(v.Packet.ReceivedAt, v.Metric!.Value)).ToArray();
        Changed(nameof(GraphPoints)); Changed(nameof(GraphStatus));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private sealed class AsyncCommand(Func<object?, Task> action, Action<Exception>? failed = null) : ICommand
    {
        private bool running;
        public bool CanExecute(object? parameter) => !running;
        public event EventHandler? CanExecuteChanged;
        public async void Execute(object? parameter)
        {
            if (running) return;
            running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await action(parameter); }
            catch (Exception e) { failed?.Invoke(e); }
            finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
}
