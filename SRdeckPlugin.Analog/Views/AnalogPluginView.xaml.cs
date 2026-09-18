using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Analog.Views;

public partial class AnalogPluginView : UserControl
{
    private readonly AnalogPluginModule? _plugin;
    private bool _isSubscribed;
    private readonly DispatcherTimer _diagnosticTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _spectrumTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public AnalogPluginView()
    {
        InitializeComponent();
        _diagnosticTimer.Tick += (_, _) => SynchronizeControls();
        _spectrumTimer.Tick += (_, _) => SynchronizeSpectrum();
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
        _spectrumTimer.Start();
        SynchronizeControls();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed || _plugin is null) return;
        _plugin.SelectedProfileChanged -= HandlePluginStateChanged;
        _plugin.ReceiverStateChanged -= HandlePluginStateChanged;
        _isSubscribed = false;
        _diagnosticTimer.Stop();
        _spectrumTimer.Stop();
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
        SignalLevelTextBlock.Text = $"{snapshot.SignalLevelDbm,4:F0} dBm";
        SignalLevelProgressBar.Value = Math.Clamp(snapshot.SignalLevelDbm + 150, 0, 150);
        SquelchThresholdTextBlock.Text = $"{snapshot.SquelchThresholdDbm,4:F0} dBm";
        SquelchThresholdProgressBar.Value = Math.Clamp(snapshot.SquelchThresholdDbm + 150, 0, 150);
        SquelchBarContainer.Opacity = snapshot.IsSquelchEnabled ? 0.85 : 0.32;
        SquelchStatusTextBlock.Text = !snapshot.IsSquelchEnabled
            ? "OFF"
            : snapshot.IsSquelchOpen ? "OPEN" : "CLOSED";
        AudioLevelTextBlock.Text = FormatDisplayDbfs(snapshot.AudioRms);

        bool isWideFm = _plugin.SelectedProfileId == "fm" && snapshot.BandwidthHz > 50_000;
        PluginProfileDescriptor? profile = _plugin.Profiles.FirstOrDefault(
            candidate => candidate.Id == _plugin.SelectedProfileId);
        ApplyReceiverFace(snapshot, isWideFm);

        StatusIndicator.Tag = snapshot.IsReceiverEnabled
            ? OverallStatusKind.Running
            : OverallStatusKind.Idle;

        string demodState = _plugin.SelectedProfileId switch
        {
            "fm" when isWideFm => snapshot.IsStereoEnabled ? "STEREO" : "MONO",
            "ssb" => snapshot.IsLowerSideband ? "LSB" : "USB",
            _ => string.Empty
        };
        DemodStateTextBlock.Text = demodState;
        DemodStateTextBlock.Visibility = string.IsNullOrEmpty(demodState)
            ? Visibility.Collapsed
            : Visibility.Visible;

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
                            snapshot.TuningStatus.Contains("拒否", StringComparison.Ordinal) ||
                            snapshot.TuningStatus.Contains("同調できません", StringComparison.Ordinal);
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

    private void SynchronizeSpectrum()
    {
        if (_plugin is null) return;
        UpdateSpectrumAnalyzer(_plugin.GetReceiverSnapshot());
    }

    private void ApplyReceiverFace(
        AnalogReceiverSnapshot snapshot,
        bool isWideFm)
    {
        MainFrequencyTextBlock.Text = snapshot.FrequencyHz > 0
            ? $"{snapshot.FrequencyHz / 1_000_000.0,8:F3}"
            : "----.---";
        ReceiverModeBadgeTextBlock.Text = _plugin?.SelectedProfileId switch
        {
            "fm" when isWideFm => "FM-WIDE",
            "fm" => "FM NARROW",
            "ssb" => "SSB",
            "am" => "AM",
            _ => "ANALOG"
        };
        DisplayBandTextBlock.Text = $"{snapshot.BandwidthHz / 1_000.0,5:F1} kHz";

        Brush off = ResolveBrush("AnalogIndicatorOffBrush");
        Brush green = ResolveBrush("AnalogIndicatorGreenBrush");
        Brush amber = ResolveBrush("AnalogIndicatorAmberBrush");
        Brush red = ResolveBrush("AnalogIndicatorRedBrush");
        MuteLamp.Background = snapshot.IsMuted ? red : off;
        AfcLamp.Background = _plugin?.SelectedProfileId == "fm" && snapshot.IsAfcEnabled
            ? amber
            : off;
        AfcIndicatorPanel.Opacity = _plugin?.SelectedProfileId == "fm" ? 1.0 : 0.32;
        SquelchLamp.Background = !snapshot.IsSquelchEnabled
            ? off
            : snapshot.IsSquelchOpen ? green : amber;
        StereoLamp.Background = isWideFm && snapshot.IsStereoEnabled && snapshot.IsStereoDetected ? green : off;
        StereoIndicatorPanel.Opacity = isWideFm ? 1 : 0.32;
    }

    private Brush ResolveBrush(string resourceKey) =>
        TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;

    private void UpdateSpectrumAnalyzer(AnalogReceiverSnapshot snapshot)
    {
        ThirdOctaveSpectrum.SetLevels(snapshot.ThirdOctaveLevelsDb);
        ThirdOctaveSpectrum.Opacity = snapshot.IsReceiverEnabled ? 1.0 : 0.28;
    }

    private static string FormatDbfs(float value) => value > 0
        ? $"{20 * Math.Log10(value):F1} dBFS"
        : "—";

    private static string FormatDisplayDbfs(float value) => value > 0
        ? $"{20 * Math.Log10(value),6:F1} dBFS"
        : "—";

    private void MainFrequency_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = OpenFrequencyInputDialogAsync();
    }

    private async void ReceiverModeBadge_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_plugin is null) return;
        string nextProfile = _plugin.SelectedProfileId switch
        {
            "am" => "fm",
            "fm" => "ssb",
            _ => "am"
        };
        await _plugin.ChangeProfileFromViewAsync(nextProfile);
    }

    private void DisplayBand_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = CycleBandwidthAsync();

    internal async ValueTask CycleBandwidthAsync()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        int[] options = _plugin.SelectedProfileId switch
        {
            "am" => [6_000, 10_000, 15_000],
            "fm" => [12_500, 15_000, 200_000],
            "ssb" => [2_400, 3_000, 4_000],
            _ => [snapshot.BandwidthHz]
        };
        int currentIndex = Array.IndexOf(options, snapshot.BandwidthHz);
        int nextBandwidth = currentIndex >= 0
            ? options[(currentIndex + 1) % options.Length]
            : (Array.Find(options, b => b > snapshot.BandwidthHz) is int found && found > 0 ? found : options[0]);

        // ponytail: cycling bandwidth presets matches standard receiver button behavior.
        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { BandwidthHz = nextBandwidth },
            requestTuning: true);
    }

    private void DemodState_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = CycleDemodStateAsync();

    internal async ValueTask CycleDemodStateAsync()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        if (_plugin.SelectedProfileId == "ssb")
        {
            await _plugin.UpdateReceiverOptionsAsync(
                options => options with { IsLowerSideband = !snapshot.IsLowerSideband },
                requestTuning: true);
        }
        else if (_plugin.SelectedProfileId == "fm" && snapshot.BandwidthHz > 50_000)
        {
            await _plugin.UpdateReceiverOptionsAsync(
                options => options with { IsStereoEnabled = !snapshot.IsStereoEnabled },
                requestTuning: false);
        }
    }

    private void MuteIndicator_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = ToggleMuteAsync();

    internal async ValueTask ToggleMuteAsync()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsMuted = !snapshot.IsMuted },
            requestTuning: false);
    }

    private void AfcIndicator_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = ToggleAfcAsync();

    internal async ValueTask ToggleAfcAsync()
    {
        if (_plugin is null || _plugin.SelectedProfileId != "fm") return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsAfcEnabled = !snapshot.IsAfcEnabled },
            requestTuning: false);
    }

    private void SquelchIndicator_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = ToggleSquelchAsync();

    internal async ValueTask ToggleSquelchAsync()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsSquelchEnabled = !snapshot.IsSquelchEnabled },
            requestTuning: false);
    }

    private void StereoIndicator_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _ = ToggleStereoAsync();

    internal async ValueTask ToggleStereoAsync()
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        if (_plugin.SelectedProfileId != "fm" || snapshot.BandwidthHz <= 50_000) return;
        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsStereoEnabled = !snapshot.IsStereoEnabled },
            requestTuning: false);
    }

    private void SquelchBar_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int delta = e.Delta > 0 ? 1 : -1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            delta *= 5;
        }
        _ = AdjustSquelchThresholdAsync(delta);
    }

    internal async ValueTask AdjustSquelchThresholdAsync(float deltaDbm)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        float current = snapshot.SquelchThresholdDbm;
        float newThreshold = Math.Clamp(MathF.Round(current + deltaDbm), -150f, 0f);
        if (Math.Abs(newThreshold - current) < 0.001f) return;

        await _plugin.UpdateReceiverOptionsAsync(
            options => options with { SquelchThresholdDbm = newThreshold },
            requestTuning: false);
    }

    internal async ValueTask<bool> OpenFrequencyInputDialogAsync(Func<FrequencyInputDialog, bool?>? showDialog = null)
    {
        if (_plugin is null) return false;
        var dialog = new FrequencyInputDialog
        {
            Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow,
            ReceiverName = "アナログ復調",
            SelectedProfileId = _plugin.SelectedProfileId ?? "am",
            ThemeBrush = TryFindResource("AnalogWideFmPrimaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x44, 0xF6, 0xDA))
        };

        bool? result = showDialog is not null ? showDialog(dialog) : dialog.ShowDialog();
        if (result == true)
        {
            bool modified = false;
            if (dialog.SelectedProfileId is { Length: > 0 } newProfile &&
                newProfile != _plugin.SelectedProfileId)
            {
                await _plugin.ChangeProfileFromViewAsync(newProfile);
                modified = true;
            }

            if (dialog.ResultFrequencyHz > 0)
            {
                await _plugin.UpdateReceiverOptionsAsync(
                    options => options with { FrequencyHz = dialog.ResultFrequencyHz },
                    requestTuning: true);
                modified = true;
            }

            return modified;
        }

        return false;
    }
}
