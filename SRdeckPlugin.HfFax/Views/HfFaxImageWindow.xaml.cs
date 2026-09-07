using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SRdeckPlugin.HfFax.Views;

public partial class HfFaxImageWindow : Window
{
    private int rotationDegrees;

    public HfFaxImageWindow(BitmapSource source, string imageInfo)
    {
        ArgumentNullException.ThrowIfNull(source);
        InitializeComponent();
        ImageView.Source = source;
        ImageInfoText.Text = imageInfo;
        UpdateRotation();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int useImmersiveDarkMode = 1;
        DwmSetWindowAttribute(handle, 20, ref useImmersiveDarkMode, sizeof(int));
    }

    private void RotateCounterClockwise_Click(object sender, RoutedEventArgs e)
    {
        rotationDegrees = (rotationDegrees + 270) % 360;
        UpdateRotation();
    }

    private void RotateClockwise_Click(object sender, RoutedEventArgs e)
    {
        rotationDegrees = (rotationDegrees + 90) % 360;
        UpdateRotation();
    }

    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        rotationDegrees = 0;
        UpdateRotation();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
        {
            rotationDegrees = (rotationDegrees + 270) % 360;
            UpdateRotation();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            rotationDegrees = (rotationDegrees + 90) % 360;
            UpdateRotation();
            e.Handled = true;
        }
    }

    private void UpdateRotation()
    {
        ImageView.LayoutTransform = new RotateTransform(rotationDegrees);
        RotationText.Text = $"{rotationDegrees}°";
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
