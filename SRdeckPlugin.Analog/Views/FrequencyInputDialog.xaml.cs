using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Analog.Views;

/// <summary>
/// 周波数・モード入力ダイアログ（demod006のFrequencyInputDialogをベースにしたアナログ復調プラグイン用ダイアログ）。
/// </summary>
public partial class FrequencyInputDialog : Window, INotifyPropertyChanged
{
    // ponytail: Standalone modal keypad dialog with mode combo. Ceiling: modal only; upgrade path: promote to SRdeckPlugin.Wpf if needed by other plugins.
    public sealed record ModeOption(string Id, string DisplayName);

    public static readonly IReadOnlyList<ModeOption> DefaultModeOptions =
    [
        new("am", "AM (振幅変調)"),
        new("fm", "FM (周波数変調)"),
        new("ssb", "SSB (単側波帯)")
    ];

    private string _inputText = string.Empty;
    private string _receiverName = "アナログ復調";
    private string _selectedProfileId = "am";
    private Brush _themeBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xF6, 0xDA));
    private string _errorText = string.Empty;
    private Visibility _errorVisibility = Visibility.Collapsed;

    public string InputText
    {
        get => _inputText;
        set
        {
            if (_inputText != value)
            {
                _inputText = value;
                OnPropertyChanged();
            }
        }
    }

    public string ReceiverName
    {
        get => _receiverName;
        set
        {
            if (_receiverName != value)
            {
                _receiverName = value;
                OnPropertyChanged();
            }
        }
    }

    public string SelectedProfileId
    {
        get => _selectedProfileId;
        set
        {
            if (_selectedProfileId != value)
            {
                _selectedProfileId = value;
                OnPropertyChanged();
                if (ModeComboBox is not null && ModeComboBox.SelectedValue as string != value)
                {
                    ModeComboBox.SelectedValue = value;
                }
            }
        }
    }

    public Brush ThemeBrush
    {
        get => _themeBrush;
        set
        {
            if (!Equals(_themeBrush, value))
            {
                _themeBrush = value;
                OnPropertyChanged();
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (_errorText != value)
            {
                _errorText = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility ErrorVisibility
    {
        get => _errorVisibility;
        set
        {
            if (_errorVisibility != value)
            {
                _errorVisibility = value;
                OnPropertyChanged();
            }
        }
    }

    public long ResultFrequencyHz { get; private set; }

    public FrequencyInputDialog()
    {
        InitializeComponent();
        DataContext = this;
        InputText = string.Empty;
        ModeComboBox.ItemsSource = DefaultModeOptions;
        ModeComboBox.SelectedValue = _selectedProfileId;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int));
        }
        catch
        {
            // DWMダークモード設定失敗時はフォールバック
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal void InputChar(string c)
    {
        if (c == "." && InputText.Contains('.'))
            return;

        InputText += c;
    }

    internal void Bs()
    {
        if (InputText.Length > 0)
        {
            InputText = InputText[..^1];
        }
    }

    internal void Clr()
    {
        InputText = string.Empty;
    }

    internal void ConfirmInput(string unit)
    {
        if (double.TryParse(InputText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            double.TryParse(InputText, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (value <= 0 || !double.IsFinite(value))
            {
                ShowError("正の数値を入力してください。");
                return;
            }

            double multiplier = unit switch
            {
                "GHz" => 1_000_000_000.0,
                "MHz" => 1_000_000.0,
                "kHz" => 1_000.0,
                "Hz" => 1.0,
                _ => 1.0
            };

            double scaled = value * multiplier;
            if (!double.IsFinite(scaled) || scaled < 1 || scaled > int.MaxValue)
            {
                ShowError("周波数は 1 Hz ～ 2,147,483,647 Hz の範囲で指定してください。");
                return;
            }

            ResultFrequencyHz = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                // ShowDialog() で開かれていない（テスト時など）場合は無視
            }
            Close();
        }
        else
        {
            if (string.IsNullOrEmpty(InputText)) return;
            ShowError("無効な周波数形式です。");
        }
    }

    private void ShowError(string message)
    {
        ErrorText = message;
        ErrorVisibility = Visibility.Visible;
        try
        {
            System.Media.SystemSounds.Hand.Play();
        }
        catch
        {
            // サウンド再生できない環境では無視
        }
    }

    private void ModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeComboBox.SelectedValue is string profileId && _selectedProfileId != profileId)
        {
            _selectedProfileId = profileId;
            OnPropertyChanged(nameof(SelectedProfileId));
        }
    }

    private void NumButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string content)
        {
            InputChar(content);
        }
    }

    private void UnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string unit)
        {
            ConfirmInput(unit);
        }
    }

    private void BsButton_Click(object sender, RoutedEventArgs e) => Bs();

    private void ClrButton_Click(object sender, RoutedEventArgs e) => Clr();

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputText))
        {
            if (SRdeckPlugin.Wpf.FrequencyInputParser.TryParse(InputText, FrequencyInputUnit.MegaHertz, out long freq))
            {
                ResultFrequencyHz = freq;
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                }
                Close();
                return;
            }
            ShowError("単位ボタン（MHz/kHz/Hz）を押すか、有効な周波数を入力してください。");
            return;
        }

        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
        }
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
        }
        Close();
    }

    private void CloseError_Click(object sender, RoutedEventArgs e)
    {
        ErrorVisibility = Visibility.Collapsed;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 1. 基本コントロールキー
        if (e.Key == Key.Escape)
        {
            if (ErrorVisibility == Visibility.Visible)
            {
                ErrorVisibility = Visibility.Collapsed;
            }
            else
            {
                CancelButton_Click(this, new RoutedEventArgs());
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back)
        {
            Bs();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            Clr();
            e.Handled = true;
            return;
        }

        // 2. 単位ショートカット
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // * キー (テンキー *, JIS :, US Shift+8) -> GHz
        if (e.Key == Key.Multiply || e.Key == Key.Oem1 || (e.Key == Key.D8 && isShift))
        {
            ConfirmInput("GHz");
            e.Handled = true;
            return;
        }
        // - キー (テンキー -, 標準 -) または M キー -> MHz
        if (e.Key == Key.Subtract || e.Key == Key.OemMinus || e.Key == Key.M)
        {
            ConfirmInput("MHz");
            e.Handled = true;
            return;
        }
        // + キー (テンキー +, 標準 + [JISならShift+;]) または K キー -> kHz
        if (e.Key == Key.Add || e.Key == Key.OemPlus || e.Key == Key.K)
        {
            ConfirmInput("kHz");
            e.Handled = true;
            return;
        }
        // Enter / Return / H キー -> Hz (または入力値があればMHzパース / モード適用)
        if (e.Key == Key.Enter || e.Key == Key.Return || e.Key == Key.H)
        {
            if (e.Key == Key.H)
            {
                ConfirmInput("Hz");
            }
            else if (!string.IsNullOrWhiteSpace(InputText))
            {
                ApplyButton_Click(this, new RoutedEventArgs());
            }
            else
            {
                ConfirmInput("Hz");
            }
            e.Handled = true;
            return;
        }

        // 3. 数字キー (修飾キーがない場合のみ入力)
        if (!isShift)
        {
            if (e.Key >= Key.D0 && e.Key <= Key.D9)
            {
                InputChar(((int)(e.Key - Key.D0)).ToString(CultureInfo.InvariantCulture));
                e.Handled = true;
            }
            else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
            {
                InputChar(((int)(e.Key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture));
                e.Handled = true;
            }
            else if (e.Key is Key.OemPeriod or Key.Decimal)
            {
                InputChar(".");
                e.Handled = true;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
