using System.Windows.Controls;

namespace SRdeckPlugin.Meshtastic.Views;

public partial class MeshtasticNodesView : UserControl
{
    public MeshtasticNodesView() => InitializeComponent();

    internal void SetDetailMaxHeight(double maxHeight) =>
        MeshtasticDetailPaneSizing.Apply(MeshtasticNodeDetailRowDefinition, MeshtasticNodeDetailPane, maxHeight);
}
