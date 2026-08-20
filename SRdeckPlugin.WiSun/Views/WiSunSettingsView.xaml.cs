using System.Windows;
using System.Windows.Controls;
using SRdeckPlugin.WiSun.ViewModels;

namespace SRdeckPlugin.WiSun.Views;

public partial class WiSunSettingsView : UserControl
{
    public WiSunSettingsView()
    {
        InitializeComponent();
    }

    private void OnStepUpClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WiSunViewModel vm)
        {
            vm.StepFrequencyUp();
        }
    }

    private void OnStepDownClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WiSunViewModel vm)
        {
            vm.StepFrequencyDown();
        }
    }
}
