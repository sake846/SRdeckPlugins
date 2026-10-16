using System.Windows;
using System.Windows.Controls;
using SRdeckPlugin.Wpf.Controls;

namespace SRdeckPlugin.Meshtastic.Views;

internal static class MeshtasticDetailPaneSizing
{
    public static void Apply(RowDefinition row, SelectionDetailPane pane, double maxHeight)
    {
        row.MaxHeight = maxHeight;
        if (row.Height.IsAbsolute && row.Height.Value > maxHeight)
            row.Height = new GridLength(maxHeight);
        pane.MaxHeight = maxHeight;
    }
}
