using System.Windows.Controls;

namespace SRdeckPlugin.Hfdl.Views;

public partial class HfdlPluginView : UserControl
{
    public HfdlPluginView() => InitializeComponent();

    private void OpenOverviewTab(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tab }) tab.IsSelected = true;
    }
}
