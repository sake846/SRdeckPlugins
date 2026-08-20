using System.Windows;
using System.Windows.Controls;
using SRdeckPlugin.WiSun.ViewModels;

namespace SRdeckPlugin.WiSun.Views;

public partial class WiSunPluginView : UserControl
{
    public WiSunPluginView()
    {
        InitializeComponent();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WiSunViewModel vm)
        {
            vm.ClearPackets();
        }
    }

    private void OnResetDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WiSunViewModel vm)
        {
            vm.ResetDiagnosticCounters();
        }
    }

    private void OnClearDiagnosticLogsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WiSunViewModel vm)
        {
            vm.ClearDiagnosticLogs();
        }
    }
}
