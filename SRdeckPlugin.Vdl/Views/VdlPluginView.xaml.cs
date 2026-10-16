using System.Windows.Controls;

namespace SRdeckPlugin.Vdl.Views;

public partial class VdlPluginView : UserControl
{
    public VdlPluginView()
    {
        InitializeComponent();
    }

    private void OpenOverviewTab(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tab }) tab.IsSelected = true;
    }
}
