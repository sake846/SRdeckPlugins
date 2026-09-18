using System.Windows.Input;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Analog.ViewModels;

public sealed class AnalogViewModel
{
    private readonly AnalogPluginModule _module;

    public AnalogViewModel(AnalogPluginModule module)
    {
        _module = module;
        ResetPluginSettingsCommand = new AsyncCommand(ResetPluginSettingsAsync);
    }

    public ICommand ResetPluginSettingsCommand { get; }

    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "Analog",
            _module.ResetSettingsAsync,
            () => { });
    }

    private sealed class AsyncCommand(Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter) => await execute();
    }
}
