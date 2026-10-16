using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Dsp;
using SRdeckPlugin.Cw.Models;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Cw.ViewModels;

public sealed partial class CwViewModel : ObservableObject
{
    private static readonly TimeSpan SignalOrderTolerance = TimeSpan.FromSeconds(5);
    private readonly Dictionary<long, CwSignalRow> rowsByTrack = [];
    private readonly Dictionary<long, CwSentenceAccumulator> sentencesByTrack = [];
    [ObservableProperty] private string status = "未初期化";
    private long centerFrequencyHz;
    private int receiveBandwidthHz = CwBandwidth.DefaultHz;
    private double minimumSnrDb = 8;
    private int maximumSignals = 24;
    private int maximumRows = 200;
    private bool monitorAudioEnabled = true;
    private int monitorAudioVolume = 50;
    private CwLanguage language = CwLanguage.Western;
    [ObservableProperty] private string captureStatus = "IQ録音: 待機";
    [ObservableProperty] private int activeSignals;
    private long processedFrames;
    private long decodedCharacters;
    private string representativeSnrText = "—";
    private string representativeSpeedText = "—";
    private bool configuring;
    private OverallStatusKind overallStatusKind = OverallStatusKind.Idle;
    private string lastDiagnosticsUpdated = "未更新";
    private IPluginRuntimeDiagnostics runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    [ObservableProperty] private CwSignalRow? selectedSignalRow;
    [ObservableProperty] private CwTimelineRow? selectedTimelineRow;
    [ObservableProperty] private string listSearchText = string.Empty;
    [ObservableProperty] private string timelineSearchText = string.Empty;

    public ObservableCollection<CwSignalRow> Rows { get; } = [];
    public ObservableCollection<CwSignalRow> FilteredRows { get; } = [];
    public IEnumerable<CwSignalRow> RecentSignals => Rows;
    public ObservableCollection<CwTimelineRow> Timeline { get; } = [];
    public ObservableCollection<CwTimelineRow> FilteredTimeline { get; } = [];
    public IReadOnlyList<CwBandwidthOption> ReceiveBandwidthOptions => CwBandwidth.Options;
    public IReadOnlyList<CwLanguage> LanguageOptions { get; } = [CwLanguage.Western, CwLanguage.Wabun];

    partial void OnListSearchTextChanged(string value) => RefreshSearchResults();
    partial void OnTimelineSearchTextChanged(string value) => RefreshSearchResults();
    public Action<CwSettings>? SettingsChanged { get; set; }
    internal Action? CaptureRequested { get; set; }

    partial void OnStatusChanged(string value)
    {
        OverallStatusKind = value.StartsWith("受信中", StringComparison.Ordinal)
            ? OverallStatusKind.Running
            : value.StartsWith("設定済み", StringComparison.Ordinal) ||
              value.StartsWith("待機中", StringComparison.Ordinal)
                ? OverallStatusKind.Info
                : OverallStatusKind.Idle;
    }

    public OverallStatusKind OverallStatusKind
    {
        get => overallStatusKind;
        private set => SetProperty(ref overallStatusKind, value);
    }

    public string SettingStatus =>
        $"{CenterFrequencyDisplay} / {CwBandwidth.Format(ReceiveBandwidthHz)} / S/N {MinimumSnrDb:F1} dB / 最大{MaximumSignals}信号 / " +
        $"{MaximumRows}行 / モニター音声 {(MonitorAudioEnabled ? $"{MonitorAudioVolume}%" : "OFF")} / {(Language == CwLanguage.Western ? "欧文" : "和文")}";

    public long ProcessedFrames { get => processedFrames; private set => SetProperty(ref processedFrames, value); }
    public long DecodedCharacters { get => decodedCharacters; private set => SetProperty(ref decodedCharacters, value); }
    public string RepresentativeSnrText { get => representativeSnrText; private set => SetProperty(ref representativeSnrText, value); }
    public string RepresentativeSpeedText { get => representativeSpeedText; private set => SetProperty(ref representativeSpeedText, value); }
    public string CenterFrequencyDisplay => $"{centerFrequencyHz / 1_000_000.0:F6} MHz";
    public string ReceiveBandwidthDisplay => CwBandwidth.Format(ReceiveBandwidthHz);
    public string OutputSampleRateDisplay =>
        $"{CwBandwidth.GetOutputSampleRateHz(ReceiveBandwidthHz) / 1_000.0:F3} kS/s";
    public string ChannelDisplay => $"対象 {CenterFrequencyDisplay} / {ReceiveBandwidthDisplay}";
    public string ProcessingParametersDisplay =>
        $"FFTサイズ {CwSkimmerReceiver.GetFftSize(ReceiveBandwidthHz):N0} / " +
        $"ホップ {CwSkimmerReceiver.GetHopSize(ReceiveBandwidthHz):N0} / " +
        $"出力 {OutputSampleRateDisplay} / 受信帯域 {ReceiveBandwidthDisplay}";
    public string LastDiagnosticsUpdated { get => lastDiagnosticsUpdated; private set => SetProperty(ref lastDiagnosticsUpdated, value); }

    public int ReceiveBandwidthHz
    {
        get => receiveBandwidthHz;
        set
        {
            if (!SetProperty(ref receiveBandwidthHz, CwBandwidth.Normalize(value))) return;
            OnPropertyChanged(nameof(SettingStatus));
            OnPropertyChanged(nameof(ReceiveBandwidthDisplay));
            OnPropertyChanged(nameof(OutputSampleRateDisplay));
            OnPropertyChanged(nameof(ChannelDisplay));
            OnPropertyChanged(nameof(ProcessingParametersDisplay));
            NotifySettingsChanged();
        }
    }

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => runtimeDiagnostics;
        set => SetProperty(ref runtimeDiagnostics, value ?? NullPluginRuntimeDiagnostics.Instance);
    }

    public long CenterFrequencyHz
    {
        get => centerFrequencyHz;
        set
        {
            if (!SetProperty(ref centerFrequencyHz, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(CenterFrequencyDisplay));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public double MinimumSnrDb
    {
        get => minimumSnrDb;
        set
        {
            if (!SetProperty(ref minimumSnrDb, Math.Clamp(value, 3, 30))) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int MaximumSignals
    {
        get => maximumSignals;
        set
        {
            if (!SetProperty(ref maximumSignals, Math.Clamp(value, 1, 32))) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int MaximumRows
    {
        get => maximumRows;
        set
        {
            if (!SetProperty(ref maximumRows, Math.Clamp(value, 25, 2_000))) return;
            OnPropertyChanged(nameof(SettingStatus));
        TrimRows();
        TrimTimeline();
        RefreshSearchResults();
            NotifySettingsChanged();
        }
    }

    public bool MonitorAudioEnabled
    {
        get => monitorAudioEnabled;
        set
        {
            if (!SetProperty(ref monitorAudioEnabled, value)) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int MonitorAudioVolume
    {
        get => monitorAudioVolume;
        set
        {
            if (!SetProperty(ref monitorAudioVolume, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public CwLanguage Language
    {
        get => language;
        set
        {
            if (!SetProperty(ref language, value)) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public void Configure(CwSettings settings, long activeCenterFrequencyHz)
    {
        configuring = true;
        try
        {
            CenterFrequencyHz = activeCenterFrequencyHz;
            ReceiveBandwidthHz = settings.ReceiveBandwidthHz;
            MinimumSnrDb = settings.MinimumSnrDb;
            MaximumSignals = settings.MaximumSignals;
            MaximumRows = settings.MaximumRows;
            MonitorAudioEnabled = settings.MonitorAudioEnabled;
            MonitorAudioVolume = settings.MonitorAudioVolume;
            Language = settings.Language;
        }
        finally { configuring = false; }
    }

    public void Apply(CwDecodeResult result)
    {
        if (!rowsByTrack.TryGetValue(result.TrackId, out CwSignalRow? row))
        {
            row = new CwSignalRow(result.TrackId);
            rowsByTrack.Add(result.TrackId, row);
            Rows.Insert(0, row);
        }
        row.Apply(result);
        StableRecencyOrder.Reorder(Rows, item => item.ReceivedAt, SignalOrderTolerance);
        DecodedCharacters += result.AppendedText.Count(character => character != ' ');
        RepresentativeSnrText = $"{result.SignalToNoiseDb:+0.0;-0.0;0.0} dB";
        RepresentativeSpeedText = result.SpeedWpm > 0 ? $"{result.SpeedWpm} WPM" : "—";
        AppendTimeline(result);
        TrimRows();
        RefreshSearchResults();
        OnPropertyChanged(nameof(RecentSignals));
    }

    public double? SelectedMonitorOffsetHz => SelectedSignalRow?.FrequencyOffsetHz;

    public void CompletePendingSentences()
    {
        foreach (CwSentenceAccumulator accumulator in sentencesByTrack.Values.ToArray())
            CommitSentence(accumulator);
        sentencesByTrack.Clear();
    }

    public void SetCenterFrequency(long value)
    {
        configuring = true;
        try { CenterFrequencyHz = value; }
        finally { configuring = false; }
    }

    public void UpdateProcessingDiagnostics(int activeSignalCount, long frameCount)
    {
        ActiveSignals = activeSignalCount;
        ProcessedFrames = frameCount;
        LastDiagnosticsUpdated = $"{DateTime.Now:HH:mm:ss}";
    }

    public void ResetProcessingDiagnostics()
    {
        ActiveSignals = 0;
        ProcessedFrames = 0;
        LastDiagnosticsUpdated = "未更新";
    }

    [RelayCommand]
    private void Clear()
    {
        SelectedSignalRow = null;
        SelectedTimelineRow = null;
        Rows.Clear();
        rowsByTrack.Clear();
        OnPropertyChanged(nameof(RecentSignals));
        Timeline.Clear();
        FilteredRows.Clear();
        FilteredTimeline.Clear();
        sentencesByTrack.Clear();
        DecodedCharacters = 0;
        RepresentativeSnrText = "—";
        RepresentativeSpeedText = "—";
    }

    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();

    private void TrimRows()
    {
        while (Rows.Count > maximumRows)
        {
            CwSignalRow row = Rows[^1];
            if (ReferenceEquals(SelectedSignalRow, row)) SelectedSignalRow = null;
            Rows.RemoveAt(Rows.Count - 1);
            rowsByTrack.Remove(row.TrackId);
        }
    }

    private void AppendTimeline(CwDecodeResult result)
    {
        if (!sentencesByTrack.TryGetValue(result.TrackId, out CwSentenceAccumulator? accumulator))
        {
            accumulator = new CwSentenceAccumulator(result.TrackId);
            sentencesByTrack.Add(result.TrackId, accumulator);
        }
        if (result.IsSentenceBoundary) CommitSentence(accumulator);
        accumulator.UpdateMetadata(result);

        foreach (char character in result.AppendedText)
        {
            accumulator.Text.Append(character);
            if (IsSentenceTerminator(character))
                CommitSentence(accumulator);
        }

        if (!result.IsFinal) return;
        CommitSentence(accumulator);
        sentencesByTrack.Remove(result.TrackId);
    }

    private void CommitSentence(CwSentenceAccumulator accumulator)
    {
        string sentence = NormalizeSentence(accumulator.Text.ToString());
        accumulator.Text.Clear();
        if (sentence.Length == 0) return;

        Timeline.Insert(0, new CwTimelineRow(
            accumulator.TrackId,
            accumulator.ReceivedAt,
            accumulator.FrequencyHz,
            accumulator.SignalToNoiseDb,
            accumulator.SpeedWpm,
            sentence));
        TrimTimeline();
    }

    private void TrimTimeline()
    {
        while (Timeline.Count > maximumRows)
            Timeline.RemoveAt(Timeline.Count - 1);
    }

    private void RefreshSearchResults()
    {
        UpdateSearchResults(Rows, FilteredRows, ListSearchText,
            static row => $"{row.TrackId} {row.FrequencyDisplay} {row.Text}");
        UpdateSearchResults(Timeline, FilteredTimeline, TimelineSearchText,
            static row => $"{row.TrackId} {row.FrequencyDisplay} {row.Text}");

        if (SelectedSignalRow is null || !FilteredRows.Contains(SelectedSignalRow))
            SelectedSignalRow = FilteredRows.FirstOrDefault();

        if (SelectedTimelineRow is null || !FilteredTimeline.Contains(SelectedTimelineRow))
            SelectedTimelineRow = FilteredTimeline.FirstOrDefault();
    }

    private static void UpdateSearchResults<T>(
        ObservableCollection<T> source, ObservableCollection<T> filtered,
        string? searchText, Func<T, string> searchableText) where T : class
    {
        string search = searchText?.Trim() ?? string.Empty;
        int targetIndex = 0;
        foreach (T row in source)
        {
            if (search.Length > 0 &&
                !searchableText(row).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (targetIndex == filtered.Count)
                filtered.Add(row);
            else if (!ReferenceEquals(filtered[targetIndex], row))
            {
                int currentIndex = targetIndex + 1;
                while (currentIndex < filtered.Count &&
                       !ReferenceEquals(filtered[currentIndex], row)) currentIndex++;
                if (currentIndex < filtered.Count) filtered.Move(currentIndex, targetIndex);
                else filtered.Insert(targetIndex, row);
            }
            targetIndex++;
        }
        while (filtered.Count > targetIndex) filtered.RemoveAt(filtered.Count - 1);
    }

    private static bool IsSentenceTerminator(char character) =>
        character is '.' or '?' or '+' or '=' or '\r' or '\n';

    private static string NormalizeSentence(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void NotifySettingsChanged()
    {
        if (configuring) return;
        SettingsChanged?.Invoke(new CwSettings(
            centerFrequencyHz, receiveBandwidthHz, minimumSnrDb, maximumSignals, maximumRows,
            monitorAudioEnabled, monitorAudioVolume, language).Normalize());
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "CW",
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
            "CW",
            () =>
            {
                Clear();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "CW",
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
                Clear();
            });
    }
}

public sealed record CwTimelineRow(
    long TrackId,
    DateTimeOffset ReceivedAt,
    long FrequencyHz,
    double SignalToNoiseDb,
    int SpeedWpm,
    string Text)
{
    public string LatestText => Text.Length <= 60 ? Text : Text[^60..];
    public DateTime LocalReceivedAt => ReceivedAt.ToLocalTime().DateTime;
    public string FrequencyDisplay => $"{FrequencyHz / 1_000.0:F3} kHz";
    public string SignalDisplay => $"{SignalToNoiseDb:+0.0;-0.0;0.0} dB";
    public string SpeedDisplay => SpeedWpm > 0 ? $"{SpeedWpm} WPM" : "—";
}

internal sealed class CwSentenceAccumulator(long trackId)
{
    public long TrackId { get; } = trackId;
    public StringBuilder Text { get; } = new();
    public DateTimeOffset ReceivedAt { get; private set; }
    public long FrequencyHz { get; private set; }
    public double SignalToNoiseDb { get; private set; }
    public int SpeedWpm { get; private set; }

    public void UpdateMetadata(CwDecodeResult result)
    {
        ReceivedAt = result.ReceivedAt;
        FrequencyHz = result.FrequencyHz;
        SignalToNoiseDb = result.SignalToNoiseDb;
        SpeedWpm = result.SpeedWpm;
    }
}

public sealed partial class CwSignalRow(long trackId) : ObservableObject
{
    private const int HistoryLimit = 20;
    private const int LatestTextCharacterCount = 60;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeDisplay))]
    private DateTimeOffset receivedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyDisplay))]
    private long frequencyHz;

    [ObservableProperty] private double signalToNoiseDb;
    [ObservableProperty] private double frequencyOffsetHz;
    [ObservableProperty] private double confidence = 1;
    [ObservableProperty] private CwLanguage language = CwLanguage.Western;
    [ObservableProperty] private int speedWpm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestText))]
    private string text = string.Empty;

    public long TrackId { get; } = trackId;
    public ObservableCollection<CwTimelineRow> History { get; } = [];
    public string LatestText => Text.Length <= LatestTextCharacterCount
        ? Text
        : Text[^LatestTextCharacterCount..];
    public string TimeDisplay => ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
    public string FrequencyDisplay => $"{FrequencyHz / 1_000.0:F3} kHz";

    public void Apply(CwDecodeResult result)
    {
        ReceivedAt = result.ReceivedAt;
        FrequencyHz = result.FrequencyHz;
        FrequencyOffsetHz = result.FrequencyOffsetHz;
        Confidence = result.Confidence;
        Language = result.Language;
        SignalToNoiseDb = result.SignalToNoiseDb;
        SpeedWpm = result.SpeedWpm;
        Text = result.Text;
        History.Insert(0, new CwTimelineRow(result.TrackId, result.ReceivedAt, result.FrequencyHz,
            result.SignalToNoiseDb, result.SpeedWpm, result.Text));
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
    }
}
