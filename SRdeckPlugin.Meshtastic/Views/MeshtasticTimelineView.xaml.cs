using System.Windows.Controls;

namespace SRdeckPlugin.Meshtastic.Views;

public partial class MeshtasticTimelineView : UserControl
{
    public MeshtasticTimelineView() => InitializeComponent();

    internal void SetDetailMaxHeight(double maxHeight) =>
        MeshtasticDetailPaneSizing.Apply(MeshtasticTimelineDetailRowDefinition, MeshtasticTimelineDetailPane, maxHeight);
}
