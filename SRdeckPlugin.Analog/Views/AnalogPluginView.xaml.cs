using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Analog.Views;

public partial class AnalogPluginView : UserControl
{
    private readonly AnalogPluginModule? _plugin;
    private bool _isSubscribed;
    private readonly DispatcherTimer _diagnosticTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public AnalogPluginView()
    {
        InitializeComponent();
        _diagnosticTimer.Tick += (_, _) => SynchronizeControls();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AnalogPluginView(AnalogPluginModule plugin) : this()
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugin = plugin;
        SettingsView.Initialize(plugin);
        SynchronizeControls();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed || _plugin is null) return;
        _plugin.SelectedProfileChanged += HandlePluginStateChanged;
        _plugin.ReceiverStateChanged += HandlePluginStateChanged;
        _isSubscribed = true;
        _diagnosticTimer.Start();
        SynchronizeControls();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed || _plugin is null) return;
        _plugin.SelectedProfileChanged -= HandlePluginStateChanged;
        _plugin.ReceiverStateChanged -= HandlePluginStateChanged;
        _isSubscribed = false;
        _diagnosticTimer.Stop();
    }

    private void HandlePluginStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(SynchronizeControls);
            return;
        }
        SynchronizeControls();
    }

    private void SynchronizeControls()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        SignalLevelTextBlock.Text = snapshot.HasCalibratedSignalLevelDbm
                ? $"{snapshot.SignalLevelDbm:F0} dBm"
                : $"{snapshot.SignalLevelDbfs:F1} dBFS";
            SignalLevelProgressBar.Value = snapshot.HasCalibratedSignalLevelDbm
                ? Math.Clamp(snapshot.SignalLevelDbm + 150, 0, 150)
                : Math.Clamp(snapshot.SignalLevelDbfs + 120, 0, 120) * 1.25;

            SquelchLamp.Background = (TryFindResource(snapshot.IsSquelchOpen ? "PluginStatusSuccessForegroundBrush" : "ControlBaseBrush") as Brush)
                ?? Brushes.Transparent;
            SquelchStatusTextBlock.Text = !snapshot.IsSquelchEnabled
                ? "無効"
                : snapshot.IsSquelchOpen ? "OPEN" : "CLOSED";
            AudioLevelTextBlock.Text = FormatDbfs(snapshot.AudioRms);

            bool isWideFm = _plugin.SelectedProfileId == "fm" && snapshot.BandwidthHz > 50_000;
            StereoLamp.Visibility = isWideFm ? Visibility.Visible : Visibility.Collapsed;
            StereoLamp.Background = (TryFindResource(snapshot.IsStereoDetected ? "PluginStatusSuccessForegroundBrush" : "ControlBaseBrush") as Brush)
                ?? Brushes.Transparent;

            StatusIndicator.Tag = snapshot.IsReceiverEnabled
                ? OverallStatusKind.Running
                : OverallStatusKind.Idle;

            PluginProfileDescriptor? profile = _plugin.Profiles.FirstOrDefault(
                candidate => candidate.Id == _plugin.SelectedProfileId);

            DemodStateTextBlock.Text = _plugin.SelectedProfileId switch
            {
                "fm" when isWideFm => snapshot.IsStereoEnabled && snapshot.IsStereoDetected ? "STEREO" : "MONO",
                "ssb" => snapshot.IsLowerSideband ? "LSB" : "USB",
                _ => profile?.DisplayName ?? _plugin.SelectedProfileId?.ToUpperInvariant() ?? "—"
            };

            string headerFrequency = snapshot.FrequencyHz > 0
                ? $"{snapshot.FrequencyHz / 1_000_000.0:F3} MHz"
                : "周波数未設定";
            string headerStatus = $"{snapshot.TuningStatus} / {profile?.DisplayName ?? _plugin.SelectedProfileId?.ToUpperInvariant() ?? "—"} / {headerFrequency}";
            StatusTextBlock.Text = headerStatus;
            StatusTextBlock.ToolTip = headerStatus;

            string profileName = profile?.DisplayName ??
                _plugin.SelectedProfileId?.ToUpperInvariant() ?? "---";
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool isStale = snapshot.MeasuredAt != default && now - snapshot.MeasuredAt > TimeSpan.FromSeconds(2);
            bool tuningFailed = snapshot.TuningStatus.Contains("失敗", StringComparison.Ordinal) ||
                                snapshot.TuningStatus.Contains("拒否", StringComparison.Ordinal);
            bool hasRecentAudio = snapshot.LastAudioOutputAt is { } audioAt && now - audioAt <= TimeSpan.FromSeconds(2);
            DiagnosticsOverview.RuntimeDiagnostics = _plugin.RuntimeDiagnostics;
            RuntimeDiagnosticsDetails.RuntimeDiagnostics = _plugin.RuntimeDiagnostics;
            DiagnosticsOverview.StatusKind = !snapshot.IsReceiverEnabled
                ? OverallStatusKind.Idle
                : tuningFailed || isStale ? OverallStatusKind.Warning
                : hasRecentAudio ? OverallStatusKind.Success
                : OverallStatusKind.Running;
            DiagnosticsOverview.StatusText = !snapshot.IsReceiverEnabled
                ? "停止中"
                : tuningFailed || isStale ? "要確認"
                : snapshot.InputSampleRateHz <= 0 ? "入力待ち"
                : hasRecentAudio ? "正常" : "監視中";
            DiagnosticsOverview.PhaseText = !snapshot.IsReceiverEnabled
                ? "段階: 停止"
                : tuningFailed ? "段階: 選局"
                : isStale ? "段階: 入力"
                : snapshot.InputSampleRateHz <= 0
                    ? "段階: 入力"
                    : "段階: 信号・音声";
            DiagnosticsOverview.LastUpdatedText = snapshot.MeasuredAt == default ? "更新 未測定" :
                $"更新 {snapshot.MeasuredAt.LocalDateTime:HH:mm:ss}";
            DiagnosticsOverview.SummaryText = !snapshot.IsReceiverEnabled
                ? "アナログ復調器は停止しています"
                : tuningFailed ? snapshot.TuningStatus
                : isStale ? "入力スナップショットが2秒以上更新されていません"
                : snapshot.InputSampleRateHz <= 0
                    ? "IQ入力を待機しています"
                    : snapshot.IsMuted ? "利用者設定により音声をミュートしています"
                    : snapshot.IsSquelchEnabled && !snapshot.IsSquelchOpen ? "入力を監視中ですが、スケルチは閉じています"
                    : hasRecentAudio ? $"{profileName}復調音声を出力しています"
                    : $"{profileName}復調信号を監視しています";
            DiagnosticsOverview.RecommendationText = tuningFailed
                ? "確認: 対象周波数、SDRの選局可能範囲、受信開始状態を確認してください"
                : isStale ? "確認: SDR入力ストリームと受信処理状態を確認してください"
                : snapshot.InputSampleRateHz <= 0
                ? "確認: SDRソース、対象周波数、受信開始状態を確認してください"
                : "確認: 必要な場合だけ入力・選局または信号・音声出力を展開してください";
            DiagnosticsOverview.ChannelText = snapshot.FrequencyHz > 0
                ? $"対象 {snapshot.FrequencyHz / 1_000_000.0:F3} MHz"
                : "対象 未設定";

            DiagnosticFrequencyTextBlock.Text = snapshot.FrequencyHz > 0
                ? $"{snapshot.FrequencyHz / 1_000_000.0:F6} MHz"
                : "--";
            DiagnosticInputRateTextBlock.Text = snapshot.InputSampleRateHz <= 0
                ? "--"
                : snapshot.InputSampleRateHz >= 1_000_000
                    ? $"{snapshot.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                    : $"{snapshot.InputSampleRateHz / 1_000.0:F1} kS/s";
            DiagnosticProfileTextBlock.Text = profileName;
            DiagnosticBandwidthTextBlock.Text = $"{snapshot.BandwidthHz / 1_000.0:F1} kHz";
            DiagnosticTuningTextBlock.Text = snapshot.TuningStatus;
            DiagnosticRateConversionTextBlock.Text = snapshot.InputSampleRateHz <= 0
                ? "—"
                : SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                    snapshot.InputSampleRateHz != snapshot.DemodulationSampleRateHz,
                    "プラグイン内部");
            DiagnosticRatePathTextBlock.Text = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
                snapshot.InputSampleRateHz, 0, snapshot.DemodulationSampleRateHz);
            DiagnosticSignalTextBlock.Text = snapshot.HasCalibratedSignalLevelDbm
                ? $"{snapshot.SignalLevelDbm:F1} dBm"
                : $"{snapshot.SignalLevelDbfs:F1} dBFS";
            DiagnosticCalibratedSignalTextBlock.Text = snapshot.HasCalibratedSignalLevelDbm
                ? $"{snapshot.SignalLevelDbm:F1} dBm"
                : "—（ホスト未校正）";
            DiagnosticSquelchTextBlock.Text = snapshot.IsSquelchEnabled
                ? $"{(snapshot.IsSquelchOpen ? "OPEN" : "CLOSED")} / しきい値 {snapshot.SquelchThresholdDbm:F1} dBm / hysteresis ±2 dB"
                : "無効";
            DiagnosticAudioTextBlock.Text = snapshot.IsMuted
                ? "ミュート"
                : hasRecentAudio ? "PCM出力中" : snapshot.IsSquelchEnabled && !snapshot.IsSquelchOpen ? "スケルチにより停止" : "出力待機中";
            DiagnosticAudioLevelTextBlock.Text = $"{FormatDbfs(snapshot.AudioRms)} / {FormatDbfs(snapshot.AudioPeak)}";
            DiagnosticPcmRateTextBlock.Text =
                $"{AnalogDemodulator.OutputSampleRateHz / 1_000.0:F1} kS/s";
            DiagnosticProfileStateTextBlock.Text = _plugin.SelectedProfileId switch
            {
                "fm" when isWideFm => $"AFC {(snapshot.IsAfcEnabled ? "有効" : "無効")} / 補正 {snapshot.AfcCorrectionHz:+0.0;-0.0;0.0} Hz / Stereo {(snapshot.IsStereoEnabled ? "有効" : "無効")} / {(snapshot.IsStereoDetected ? "検出" : "未検出")}",
                "fm" => $"AFC {(snapshot.IsAfcEnabled ? "有効" : "無効")} / 補正 {snapshot.AfcCorrectionHz:+0.0;-0.0;0.0} Hz / Narrow FM",
                "ssb" => $"{(snapshot.IsLowerSideband ? "LSB" : "USB")} / BFOオフセットは対象周波数との差分を使用",
                _ => "AM包絡線復調"
            };
    }

    private static string FormatDbfs(float value) => value > 0
        ? $"{20 * Math.Log10(value):F1} dBFS"
        : "—";
}
