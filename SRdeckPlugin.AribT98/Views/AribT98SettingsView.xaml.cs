using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SRdeckPlugin.AribT98.ViewModels;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AribT98.Views;

public partial class AribT98SettingsView : UserControl
{
    private bool autoApplyQueued;
    private bool frequencyEditing;

    public AribT98SettingsView()
    {
        InitializeComponent();
        SettingsBody.LostKeyboardFocus += OnSettingsLostKeyboardFocus;
        SettingsBody.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnSettingsButtonClick), true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnSettingsLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => QueueAutoApply();

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => QueueAutoApply();

    private void FrequencyTextBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!frequencyEditing) BeginFrequencyEditing();
    }

    private void FrequencyTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        BeginFrequencyEditing();
    }

    private void BeginFrequencyEditing()
    {
        frequencyEditing = true;
        FrequencyTextBox.ToolTip = "数字＋M/m: MHz、数字＋K/k: kHz、Enter: Hz";
        FrequencyTextBox.Clear();
    }

    private void FrequencyTextBox_OnKeyDown(object sender, KeyEventArgs e)
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
        ApplyFrequency(unit.Value);
    }

    private void FrequencyTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (frequencyEditing) ApplyFrequency(FrequencyInputUnit.Hz);
    }

    private void ApplyFrequency(FrequencyInputUnit unit)
    {
        if (DataContext is not AribT98ViewModel viewModel) return;
        if (string.IsNullOrWhiteSpace(FrequencyTextBox.Text))
        {
            frequencyEditing = false;
            BindingOperations.GetBindingExpression(FrequencyTextBox, TextBox.TextProperty)?.UpdateTarget();
            return;
        }
        if (!SRdeckPlugin.Wpf.FrequencyInputParser.TryParse(FrequencyTextBox.Text, unit, out long frequencyHz) || frequencyHz < 1_000_000)
        {
            FrequencyTextBox.ToolTip = "受信周波数を1 MHz以上で入力してください。";
            return;
        }

        frequencyEditing = false;
        FrequencyTextBox.ToolTip = "数字＋M/m: MHz、数字＋K/k: kHz、Enter: Hz";
        viewModel.FrequencyHz = frequencyHz;
        BindingOperations.GetBindingExpression(FrequencyTextBox, TextBox.TextProperty)?.UpdateTarget();
        QueueAutoApply();
    }

    private void QueueAutoApply()
    {
        if (autoApplyQueued) return;
        autoApplyQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            autoApplyQueued = false;
            if (DataContext is AribT98ViewModel viewModel && viewModel.HasPendingSettings && !viewModel.ApplyCommand.IsRunning)
                viewModel.ApplyCommand.Execute(null);
        }, DispatcherPriority.Background);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Startup caches the plugin views before MainWindow exists. An ancestor
        // binding evaluated then can remain unresolved even after attachment.
        // Bind to the actual hosting window each time this view is loaded.
        if (Window.GetWindow(this) is not { } window) return;
        SettingsBody.SetBinding(VisibilityProperty, new Binding("DataContext.IsPluginSettingsExpanded")
        {
            Source = window,
            Mode = BindingMode.OneWay,
            Converter = new BooleanToVisibilityConverter(),
            FallbackValue = Visibility.Visible
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        // Cached settings views must not retain a closed settings-dialog window.
        BindingOperations.ClearBinding(SettingsBody, VisibilityProperty);
}
