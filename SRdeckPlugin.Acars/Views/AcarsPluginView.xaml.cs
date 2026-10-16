using System.Windows.Controls;

namespace SRdeckPlugin.Acars.Views;

public partial class AcarsPluginView : UserControl
{
    public AcarsPluginView() => InitializeComponent();

    private void OpenOverviewTab(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tab }) tab.IsSelected = true;
    }
}
