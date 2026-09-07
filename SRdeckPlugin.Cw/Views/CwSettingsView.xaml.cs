using System.Windows.Controls;

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SRdeckPlugin.Cw.ViewModels;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Cw.Views;

public partial class CwSettingsView : UserControl
{
    private const string FrequencyInputHint = "M: MHz / K: kHz / Enter: Hz";
    private bool isSynchronizing;
    private bool isFrequencyEditing;
    private CwViewModel? subscribedViewModel;

    public CwSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Subscribe(e.NewValue as CwViewModel);
        SynchronizeFrequencyText(force: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe(DataContext as CwViewModel);
        SynchronizeFrequencyText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Subscribe(null);

    private void Subscribe(CwViewModel? viewModel)
    {
        if (ReferenceEquals(subscribedViewModel, viewModel)) return;
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        subscribedViewModel = viewModel;
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CwViewModel.CenterFrequencyHz)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SynchronizeFrequencyText());
            return;
        }
        SynchronizeFrequencyText();
    }

    private void FrequencyTextBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!isSynchronizing && !isFrequencyEditing)
            BeginFrequencyEditing();
    }

    private void FrequencyTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!isSynchronizing)
            BeginFrequencyEditing();
    }

    private void BeginFrequencyEditing()
    {
        if (isFrequencyEditing) return;
        isFrequencyEditing = true;
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
        if (isSynchronizing || !isFrequencyEditing) return;
        ApplyFrequency(FrequencyInputUnit.Hz);
    }

    private void ApplyFrequency(FrequencyInputUnit unit)
    {
        if (subscribedViewModel is null) return;
        if (string.IsNullOrWhiteSpace(FrequencyTextBox.Text))
        {
            SynchronizeFrequencyText(force: true);
            return;
        }
        if (!FrequencyInputParser.TryParse(FrequencyTextBox.Text, unit, out long frequencyHz))
        {
            FrequencyInputStatusTextBlock.Text =
                "1～2,147,483,647 Hzで入力してください";
            return;
        }

        subscribedViewModel.CenterFrequencyHz = frequencyHz;
        FrequencyInputStatusTextBlock.Text = FrequencyInputHint;
        SynchronizeFrequencyText(force: true);
    }

    private void SynchronizeFrequencyText(bool force = false)
    {
        if (subscribedViewModel is null) return;
        if (!force && FrequencyTextBox.IsKeyboardFocusWithin) return;
        isSynchronizing = true;
        try
        {
            FrequencyTextBox.Text = subscribedViewModel.CenterFrequencyHz > 0
                ? subscribedViewModel.CenterFrequencyHz.ToString("N0")
                : string.Empty;
            if (force) isFrequencyEditing = false;
        }
        finally
        {
            isSynchronizing = false;
        }
    }
}
