using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Analog.Views;

public partial class AnalogSettingsView : UserControl
{
    private static readonly IReadOnlyList<NumericOption> StepOptions =
    [
        new(10, "10 Hz"), new(100, "100 Hz"), new(500, "500 Hz"),
        new(1_000, "1 kHz"), new(5_000, "5 kHz"), new(6_250, "6.25 kHz"),
        new(8_333, "8.33 kHz"), new(9_000, "9 kHz"), new(10_000, "10 kHz"),
        new(12_500, "12.5 kHz"), new(25_000, "25 kHz"),
        new(50_000, "50 kHz"), new(100_000, "100 kHz")
    ];

    private AnalogPluginModule? _plugin;
    private bool _isSynchronizing;
    private bool _isFrequencyEditing;
    private bool _isSubscribed;

    public AnalogSettingsView()
    {
        InitializeComponent();
        StepComboBox.ItemsSource = StepOptions;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AnalogSettingsView(AnalogPluginModule plugin) : this()
    {
        Initialize(plugin);
    }

    public void Initialize(AnalogPluginModule plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_plugin == plugin) return;
        if (_plugin is not null && _isSubscribed)
        {
            _plugin.SelectedProfileChanged -= HandlePluginStateChanged;
            _plugin.ReceiverStateChanged -= HandlePluginStateChanged;
            _plugin.CaptureStatusChanged -= HandlePluginStateChanged;
            _isSubscribed = false;
        }
        _plugin = plugin;
        ProfileComboBox.ItemsSource = plugin.Profiles;
        if (IsLoaded && !_isSubscribed)
        {
            _plugin.SelectedProfileChanged += HandlePluginStateChanged;
            _plugin.ReceiverStateChanged += HandlePluginStateChanged;
            _plugin.CaptureStatusChanged += HandlePluginStateChanged;
            _isSubscribed = true;
        }
        SynchronizeControls();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed || _plugin is null) return;
        _plugin.SelectedProfileChanged += HandlePluginStateChanged;
        _plugin.ReceiverStateChanged += HandlePluginStateChanged;
        _plugin.CaptureStatusChanged += HandlePluginStateChanged;
        _isSubscribed = true;
        SynchronizeControls();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed || _plugin is null) return;
        _plugin.SelectedProfileChanged -= HandlePluginStateChanged;
        _plugin.ReceiverStateChanged -= HandlePluginStateChanged;
        _plugin.CaptureStatusChanged -= HandlePluginStateChanged;
        _isSubscribed = false;
    }

    private async void FrequencyTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || !_isFrequencyEditing) return;
        await ApplyFrequencyAsync();
    }

    private void FrequencyTextBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isSynchronizing && !_isFrequencyEditing)
            BeginFrequencyEditing();
    }

    private void FrequencyTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isSynchronizing)
            BeginFrequencyEditing();
    }

    private void BeginFrequencyEditing()
    {
        _isFrequencyEditing = true;
        FrequencyTextBox.Clear();
    }

    private async void FrequencyTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        FrequencyInputUnit? unit = e.Key switch
        {
            Key.Enter or Key.Return => FrequencyInputUnit.Hz,
            Key.M => FrequencyInputUnit.MegaHertz,
            Key.K => FrequencyInputUnit.KiloHertz,
            _ => null
        };
        if (unit is null) return;
        e.Handled = true;
        await ApplyFrequencyAsync(unit.Value);
    }

    private async Task ApplyFrequencyAsync(FrequencyInputUnit unit = FrequencyInputUnit.Hz)
    {
        if (_plugin is null) return;
        if (string.IsNullOrWhiteSpace(FrequencyTextBox.Text))
        {
            SynchronizeControls(forceFrequencyText: true);
            return;
        }
        if (!FrequencyInputParser.TryParse(FrequencyTextBox.Text, unit, out long frequencyHz))
        {
            StatusTextBlock.Text = "受信周波数を1～2,147,483,647 Hzで入力してください。";
            return;
        }
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { FrequencyHz = frequencyHz },
            requestTuning: true), synchronizeFrequencyText: true);
    }

    private async void StepDownButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        await RunChangeAsync(() => _plugin.AdjustFrequencyAsync(-snapshot.StepHz));
    }

    private async void StepUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        await RunChangeAsync(() => _plugin.AdjustFrequencyAsync(snapshot.StepHz));
    }

    private async void ProfileComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null || ProfileComboBox.SelectedValue is not string profileId) return;
        await RunChangeAsync(() => _plugin.ChangeProfileFromViewAsync(profileId));
    }

    private async void StepComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null || StepComboBox.SelectedValue is not int stepHz) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { StepHz = stepHz }, requestTuning: false));
    }

    private async void BandwidthComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null || BandwidthComboBox.SelectedValue is not int bandwidthHz) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { BandwidthHz = bandwidthHz }, requestTuning: true));
    }

    private async void SidebandComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null || SidebandComboBox.SelectedItem is not ComboBoxItem item ||
            !bool.TryParse(item.Tag?.ToString(), out bool lower)) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsLowerSideband = lower }, requestTuning: false));
    }

    private async void MuteButton_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsMuted = MuteButton.IsChecked == true }, false));
    }

    private async void AfcButton_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsAfcEnabled = AfcButton.IsChecked == true }, false));
    }

    private async void StereoButton_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsStereoEnabled = StereoButton.IsChecked == true }, false));
    }

    private async void SquelchButton_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _plugin is null) return;
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { IsSquelchEnabled = SquelchButton.IsChecked == true }, false));
    }

    private async void SquelchThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSynchronizing || _plugin is null) return;
        float newThreshold = MathF.Round((float)e.NewValue);
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { SquelchThresholdDbm = Math.Clamp(newThreshold, -150f, 0f) }, false),
            synchronizeSquelchText: true);
    }

    private void SquelchThresholdTextBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isSynchronizing && !SquelchThresholdTextBox.IsKeyboardFocusWithin)
        {
            SquelchThresholdTextBox.Focus();
        }
    }

    private void SquelchThresholdTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isSynchronizing)
        {
            SquelchThresholdTextBox.SelectAll();
        }
    }

    private async void SquelchThresholdTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing) return;
        await ApplySquelchAsync();
    }

    private async void SquelchThresholdTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplySquelchAsync();
    }

    private async Task ApplySquelchAsync()
    {
        if (_plugin is null) return;
        if (!FrequencyInputParser.TryParseSquelchThreshold(SquelchThresholdTextBox.Text, out float threshold))
        {
            StatusTextBlock.Text = "SQしきい値を-150～0 dBm（例: -80 や 80）で入力してください。";
            SynchronizeControls(forceSquelchText: true);
            return;
        }
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { SquelchThresholdDbm = threshold }, false), synchronizeSquelchText: true);
    }

    private async Task RunChangeAsync(Func<ValueTask> change, bool synchronizeFrequencyText = false, bool synchronizeSquelchText = false)
    {
        SetControlsEnabled(false);
        StatusTextBlock.Text = "設定を適用しています…";
        try
        {
            await change();
            SynchronizeControls(synchronizeFrequencyText, synchronizeSquelchText);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"設定を適用できませんでした: {exception.Message}";
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void HandlePluginStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SynchronizeControls());
            return;
        }
        SynchronizeControls();
    }

    private void StartCaptureButton_OnClick(object sender, RoutedEventArgs e) =>
        _plugin?.StartIqCapture();

    private void SynchronizeControls(bool forceFrequencyText = false, bool forceSquelchText = false)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        _isSynchronizing = true;
        try
        {
            ProfileComboBox.SelectedValue = _plugin.SelectedProfileId;
            StepComboBox.SelectedValue = snapshot.StepHz;
            SetBandwidthOptions(snapshot.BandwidthHz);
            bool isSsb = _plugin.SelectedProfileId == "ssb";
            SidebandLabel.Visibility = isSsb ? Visibility.Visible : Visibility.Collapsed;
            SidebandComboBox.Visibility = isSsb ? Visibility.Visible : Visibility.Collapsed;
            SidebandComboBox.SelectedIndex = snapshot.IsLowerSideband ? 1 : 0;
            bool isWideFm = _plugin.SelectedProfileId == "fm" && snapshot.BandwidthHz > 50_000;
            AfcButton.IsEnabled = _plugin.SelectedProfileId == "fm";
            AfcButton.IsChecked = snapshot.IsAfcEnabled;
            StereoButton.IsEnabled = isWideFm;
            StereoButton.IsChecked = snapshot.IsStereoEnabled;
            MuteButton.IsChecked = snapshot.IsMuted;
            SquelchButton.IsChecked = snapshot.IsSquelchEnabled;

            // スライダーとしきい値テキスト
            SquelchThresholdSlider.IsEnabled = snapshot.IsSquelchEnabled;
            SquelchThresholdSlider.Value = snapshot.SquelchThresholdDbm;
            if (forceSquelchText || !SquelchThresholdTextBox.IsKeyboardFocusWithin)
            {
                SquelchThresholdTextBox.Text = snapshot.SquelchThresholdDbm.ToString("F0", CultureInfo.CurrentCulture);
            }
            if (forceFrequencyText || !FrequencyTextBox.IsKeyboardFocusWithin)
            {
                FrequencyTextBox.Text = snapshot.FrequencyHz > 0 ? snapshot.FrequencyHz.ToString("N0") : string.Empty;
                if (forceFrequencyText)
                    _isFrequencyEditing = false;
            }
            StatusTextBlock.Text = snapshot.TuningStatus;
            StatusTextBlock.ToolTip = snapshot.TuningStatus;
            CaptureStatusTextBlock.Text = _plugin.CaptureStatus;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SetBandwidthOptions(int selectedBandwidth)
    {
        IReadOnlyList<NumericOption> options = _plugin?.SelectedProfileId switch
        {
            "am" => [new(6_000, "6 kHz"), new(10_000, "10 kHz"), new(15_000, "15 kHz")],
            "fm" => [new(12_500, "12.5 kHz"), new(15_000, "15 kHz"), new(200_000, "200 kHz")],
            "ssb" => [new(2_400, "2.4 kHz"), new(3_000, "3 kHz"), new(4_000, "4 kHz")],
            _ => [new(selectedBandwidth, $"{selectedBandwidth:N0} Hz")]
        };
        if (!options.Any(option => option.Value == selectedBandwidth))
            options = [.. options, new(selectedBandwidth, $"{selectedBandwidth:N0} Hz")];
        BandwidthComboBox.ItemsSource = options;
        BandwidthComboBox.SelectedValue = selectedBandwidth;
    }

    private async void SquelchDownButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        float newThreshold = MathF.Round(snapshot.SquelchThresholdDbm - 1f);
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { SquelchThresholdDbm = Math.Clamp(newThreshold, -150f, 0f) }, false), synchronizeSquelchText: true);
    }

    private async void SquelchUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        AnalogReceiverSnapshot snapshot = _plugin.GetReceiverSnapshot();
        float newThreshold = MathF.Round(snapshot.SquelchThresholdDbm + 1f);
        await RunChangeAsync(() => _plugin.UpdateReceiverOptionsAsync(
            options => options with { SquelchThresholdDbm = Math.Clamp(newThreshold, -150f, 0f) }, false), synchronizeSquelchText: true);
    }

    private void SetControlsEnabled(bool enabled)
    {
        ProfileComboBox.IsEnabled = enabled;
        FrequencyTextBox.IsEnabled = enabled;
        StepComboBox.IsEnabled = enabled;
        BandwidthComboBox.IsEnabled = enabled;
        SidebandComboBox.IsEnabled = enabled;
        SquelchThresholdSlider.IsEnabled = enabled && (SquelchButton.IsChecked == true);
        SquelchThresholdTextBox.IsEnabled = enabled;
        SquelchDownButton.IsEnabled = enabled;
        SquelchUpButton.IsEnabled = enabled;
    }

    private sealed record NumericOption(int Value, string Label);
}
