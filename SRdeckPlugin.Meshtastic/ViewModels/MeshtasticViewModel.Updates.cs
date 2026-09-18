using System.Collections.ObjectModel;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Wpf;
using System.Globalization;

namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private readonly object _diagnosticGate = new();
    private LoRaPreambleDetection? _pendingDetection;
    private LoRaFrameSynchronization? _pendingSynchronization;
    private string? _pendingFailure;
    private DispatcherTimer? _statisticsTimer;
    private MeshtasticWorkerFailure? _lastWorkerFailure;
    private MeshtasticStateWriter? _stateWriter;
    private int _presentationEpoch;
    private long _historyVersion;
    private bool _historyRefreshRunning;
    private bool _restoringMeshtasticState;

    private void StartMeshtasticUpdates()
    {
        IPluginHostContext? context = _hostContext;
        if (context is null) return;
        void StartOnUi()
        {
            if (Volatile.Read(ref _initialized) == 0 || !ReferenceEquals(context, _hostContext)) return;
            CreateMeshtasticStatisticsTimer();
        }
        if (context.Dispatcher.CheckAccess()) StartOnUi();
        else context.Dispatcher.Post(StartOnUi);
    }

    private void CreateMeshtasticStatisticsTimer()
    {
        _statisticsTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(250) };
        _statisticsTimer.Tick += (_, _) => ApplyPendingMeshtasticDiagnostics();
        _statisticsTimer.Start();
    }

    internal void ApplyPendingMeshtasticDiagnostics()
    {
        LoRaPreambleDetection? detection;
        LoRaFrameSynchronization? synchronization;
        string? failure;
        lock (_diagnosticGate)
        {
            detection = _pendingDetection;
            synchronization = _pendingSynchronization;
            failure = _pendingFailure;
            _pendingDetection = null;
            _pendingSynchronization = null;
            _pendingFailure = null;
        }
        if (detection is not null)
            MeshtasticLastSignalStatus = $"最終信号: {FormatMeshtasticSignal(detection.EstimatedSnrDb, detection.EstimatedRssiDbfs)}（推定） / dechirpピーク {detection.DechirpedPeakHz:F1} Hz";
        if (synchronization is not null)
        {
            MeshtasticFrequencyCorrectionText = FormatMeshtasticFrequencyCorrection(synchronization);
            MeshtasticLastSignalStatus = $"最終信号: {FormatMeshtasticSignal(synchronization.EstimatedSnrDb, synchronization.EstimatedRssiDbfs)}（推定）";
        }
        if (failure is not null) MeshtasticLastFailureStatus = failure;
        ApplyMeshtasticStatistics(_meshtasticReceiveService.Snapshot);
        _meshtasticDiagnosticCache = null;
        OnPropertyChanged(nameof(MeshtasticDiagnosticStatusKind));
        OnPropertyChanged(nameof(MeshtasticDiagnosticStatusText));
        OnPropertyChanged(nameof(MeshtasticDiagnosticPhase));
        OnPropertyChanged(nameof(MeshtasticDiagnosticSummary));
        OnPropertyChanged(nameof(MeshtasticDiagnosticRecommendation));
        OnPropertyChanged(nameof(MeshtasticDiagnosticLastUpdated));
        OnPropertyChanged(nameof(MeshtasticDiagnosticSignalText));
    }

    private void HandleMeshtasticWorkerFailed(MeshtasticWorkerFailure failure) =>
        _hostContext?.Logger.Log(PluginLogLevel.Error, "meshtastic.worker.failed",
            $"Meshtastic decoder stopped at block {failure.Sequence}, sample {failure.AbsoluteSampleStart}.", failure.Exception);

    private Task InvokeMeshtasticUiAsync(Action action)
    {
        IPluginDispatcher? dispatcher = _hostContext?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private static void ReplaceMeshtasticItems<T>(ObservableCollection<T> collection,
        IReadOnlyList<T> values, Func<T, T, bool>? equals = null) =>
        ((MeshtasticDisplayCollection<T>)collection).ReplaceWith(values, equals);

    private void UpdateMeshtasticMapMarker(MeshtasticMapPoint point)
    {
        string id = point.NodeNumber.ToString(CultureInfo.InvariantCulture);
        int index = -1;
        for (int i = 0; i < MapMarkers.Count; i++)
            if (MapMarkers[i].Id == id) { index = i; break; }
        if ((MeshtasticMapActiveOnly && point.ActivityStatus is not ("新規" or "活動中")) ||
            (MeshtasticMapDirectOnly && !point.HasDirectReception))
        {
            if (index >= 0) MapMarkers.RemoveAt(index);
            return;
        }
        string reception = point.HasDirectReception ? "直接受信あり" : "中継受信のみ";
        var marker = new GeoMapMarker(id, point.Latitude, point.Longitude, point.Label,
            $"{point.ActivityStatus} / {reception}\n{point.Coordinates}",
            MeshtasticMapPresentation.GetNodeColor(point.ActivityStatus),
            Symbol: "station", IsSelected: point.IsSelected);
        if (index < 0) MapMarkers.Add(marker);
        else if (MapMarkers[index] != marker) MapMarkers[index] = marker;
    }

    private async Task RefreshMeshtasticHistoryInBackground()
    {
        if (_historyRefreshRunning) return;
        _historyRefreshRunning = true;
        long version = _historyVersion;
        int epoch = _presentationEpoch;
        MeshtasticDisplayItem[] messages = MeshtasticMessages.ToArray();
        int activeNodes = MeshtasticNodes.Count(node => node.ActivityStatus is "新規" or "活動中");
        string search = MeshtasticHistorySearchText;
        bool decoded = MeshtasticHistoryDecodedOnly, direct = MeshtasticHistoryDirectOnly;
        int limit = MeshtasticHistoryDisplayLimit;
        try
        {
            MeshtasticHistoryAnalysisResult result = await Task.Run(() =>
                _meshtasticHistoryAnalyzer.Analyze(messages, activeNodes, search, decoded, direct, limit))
                .ConfigureAwait(false);
            await InvokeMeshtasticUiAsync(() =>
            {
                if (epoch == _presentationEpoch && version == _historyVersion)
                    ApplyMeshtasticHistoryAnalysis(result);
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _hostContext?.Logger.Log(PluginLogLevel.Warning, "meshtastic.history.analysis-failed",
                "Meshtastic history display could not be refreshed.", exception);
        }
        finally
        {
            await InvokeMeshtasticUiAsync(() =>
            {
                _historyRefreshRunning = false;
                if (epoch == _presentationEpoch && version != _historyVersion)
                    ScheduleMeshtasticDerivedRefresh();
            }).ConfigureAwait(false);
        }
    }
}
