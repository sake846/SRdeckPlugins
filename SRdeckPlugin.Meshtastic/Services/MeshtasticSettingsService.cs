using System;
using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Dsp;

using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Meshtastic.Services;

internal sealed record PersistedPluginSettings(
    MeshtasticRegion Region,
    MeshtasticModemPreset ModemPreset,
    bool IsDiscoveryMode,
    int RadioChannel,
    string RadioChannels250,
    string RadioChannels125,
    int HistoryDisplayLimit,
    int HistoryRetentionDays,
    MeshtasticModemPreset LastSpecifiedModemPreset = MeshtasticModemPreset.LongFast,
    GeoMapState? MapState = null,
    bool IsAudioMonitorEnabled = true,
    bool IsSquelchEnabled = true,
    bool BuzzerEnabled = true);

/// <summary>
/// Owns Meshtastic settings persistence and data-file locations.
/// It deliberately does not know about ViewModel properties or WPF collections.
/// </summary>
internal sealed class MeshtasticSettingsService
{
    private IPluginHostContext? _hostContext;

    public string PluginDataDirectory => _hostContext?.Settings.DataDirectory ?? DefaultPluginDataDirectory;
    public string StatePath => Path.Combine(PluginDataDirectory, "state.json");
    public string HistoryPath => Path.Combine(PluginDataDirectory, "meshtastic-history.jsonl");

    private static string DefaultPluginDataDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(appData)
                ? Path.Combine(AppContext.BaseDirectory, "SRdeck", "plugins", "meshtastic")
                : Path.Combine(appData, "SRdeck", "plugins", "meshtastic");
        }
    }

    public void Attach(IPluginHostContext hostContext) => _hostContext = hostContext;

    public void Detach() => _hostContext = null;

    public PersistedPluginSettings Load()
    {
        try
        {
            PluginSettingsDocument? document = _hostContext?.Settings.LoadAsync()
                .AsTask().GetAwaiter().GetResult();
            if (document is not null)
            {
                PersistedPluginSettings? persisted = JsonSerializer.Deserialize<PersistedPluginSettings>(document.Json);
                if (persisted is not null) return persisted;
            }
        }
        catch (Exception exception)
        {
            _hostContext?.Logger.Log(
                PluginLogLevel.Warning,
                "settings.load",
                "Meshtastic plugin settings could not be loaded; default settings will be used.",
                exception);
        }

        return new PersistedPluginSettings(
            MeshtasticRegion.JP,
            MeshtasticModemPreset.LongFast,
            true,
            MeshtasticJpLongFastProfile.DefaultChannel,
            MeshtasticJpLongFastProfile.DefaultChannel.ToString(),
            MeshtasticJpLongFastProfile.DefaultChannel.ToString(),
            10_000,
            90);
    }

    public void Save(PersistedPluginSettings settings)
    {
        if (_hostContext is null) return;

        string json = JsonSerializer.Serialize(settings);
        _hostContext.Settings.SaveAsync(new PluginSettingsDocument(
                1,
                json))
            .AsTask().GetAwaiter().GetResult();
    }

}
