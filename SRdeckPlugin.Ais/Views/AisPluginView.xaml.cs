using System.Windows.Controls;

namespace SRdeckPlugin.Ais.Views;

public partial class AisPluginView : UserControl
{
    public AisPluginView() => InitializeComponent();

    private void OpenOverviewTab(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tab }) tab.IsSelected = true;
    }
}
