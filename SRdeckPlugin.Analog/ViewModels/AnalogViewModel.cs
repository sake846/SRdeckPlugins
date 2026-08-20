using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Analog.Dsp;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Analog.ViewModels;

public sealed partial class AnalogViewModel : ObservableObject
{
    private readonly AnalogPluginModule _module;

    public AnalogPluginModule Module => _module;

    public AnalogViewModel(AnalogPluginModule module)
    {
        _module = module;
        _module.SelectedProfileChanged += (s, e) => OnPropertyChanged(nameof(SelectedProfileId));
        _module.ReceiverStateChanged += (s, e) => OnPropertyChanged(nameof(CaptureStatus));
    }

    public string? SelectedProfileId => _module.SelectedProfileId;
    public string CaptureStatus => _module.CaptureStatus;

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "Analog",
            async () =>
            {
                await _module.ResetSettingsAsync();
            },
            () => { });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        PluginResetHelper.ConfirmAndClearData(
            "Analog",
            () => { });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "Analog",
            async () =>
            {
                await _module.ResetSettingsAsync();
            },
            () => { },
            () => { });
    }
}
