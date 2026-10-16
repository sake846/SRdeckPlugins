using System.Windows;
using System.Windows.Controls;

// View exported by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Views;

public partial class MeshtasticPluginView : UserControl
{
    public MeshtasticPluginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDetailPanesMaxHeight(MeshtasticReceiverPanel?.ActualHeight ?? 0);
    }

    private void OnReceiverPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDetailPanesMaxHeight(e.NewSize.Height);
    }

    public void UpdateDetailPanesMaxHeight(double receiverHeight)
    {
        if (receiverHeight <= 0) return;
        double maxHeight = Math.Max(0, receiverHeight * 0.5);

        NodesView?.SetDetailMaxHeight(maxHeight);
        TimelineView?.SetDetailMaxHeight(maxHeight);
    }
}
