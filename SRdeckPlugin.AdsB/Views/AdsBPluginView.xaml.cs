using System.Windows.Controls;

namespace SRdeckPlugin.AdsB.Views;

public partial class AdsBPluginView : UserControl
{
    public AdsBPluginView() => InitializeComponent();

    private void OpenOverviewTab(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tab }) tab.IsSelected = true;
    }
}
