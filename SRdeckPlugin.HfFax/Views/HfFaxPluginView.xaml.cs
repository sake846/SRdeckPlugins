using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SRdeckPlugin.HfFax.Models;
using SRdeckPlugin.HfFax.ViewModels;

namespace SRdeckPlugin.HfFax.Views;

public partial class HfFaxPluginView : UserControl
{
    private HfFaxImageWindow? imageWindow;

    public HfFaxPluginView() => InitializeComponent();

    private void CurrentImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not HfFaxViewModel viewModel) return;
        BitmapSource? source = viewModel.CreateCurrentImageForPopup();
        if (source is null) return;
        ShowImagePopup(source, $"HF-FAX / {viewModel.CurrentImageDimensions}");
        e.Handled = true;
    }

    private void HistoryImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HfFaxHistoryRow row }) return;
        ShowImagePopup(HfFaxViewModel.CreateImageSource(row.Snapshot),
            $"HF-FAX / {row.SizeDisplay} / {row.FrequencyDisplay}");
        e.Handled = true;
    }

    private void ShowImagePopup(BitmapSource source, string imageInfo)
    {
        imageWindow?.Close();
        imageWindow = new HfFaxImageWindow(source, imageInfo)
        {
            Owner = Window.GetWindow(this)
        };
        imageWindow.Closed += (_, _) => imageWindow = null;
        imageWindow.Show();
    }
}
