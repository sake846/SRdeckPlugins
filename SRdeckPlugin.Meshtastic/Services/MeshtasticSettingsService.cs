using System.Globalization;
using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Dsp;

using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Meshtastic.Services;

internal sealed record PersistedPluginSettings(
    MeshtasticRegion Region = MeshtasticRegion.JP,
    MeshtasticModemPreset ModemPreset = MeshtasticModemPreset.LongFast,
    bool IsDiscoveryMode = true,
    int RadioChannel = MeshtasticJpLongFastProfile.DefaultChannel,
    string RadioChannels250 = "",
    string RadioChannels125 = "",
    int HistoryDisplayLimit = 10_000,
    int HistoryRetentionDays = 90,
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
    private const int MinimumHistoryDisplayLimit = 100;
    private const int MaximumHistoryDisplayLimit = 100_000;
    private const int MinimumHistoryRetentionDays = 1;
    private const int MaximumHistoryRetentionDays = 3650;
    private readonly object _gate = new();
    private IPluginHostContext? _hostContext;

    public string PluginDataDirectory => _hostContext?.Settings.DataDirectory ?? DefaultPluginDataDirectory;
    public string StatePath => Path.Combine(PluginDataDirectory, "state.json");
    public string HistoryPath => Path.Combine(PluginDataDirectory, "meshtastic-history.jsonl");

    internal static string DefaultPluginDataDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(appData)
                ? Path.Combine(AppContext.BaseDirectory, "SRdeck", "plugins", "meshtastic")
                : Path.Combine(appData, "SRdeck", "plugins", "meshtastic");
        }
    }

    public void Attach(IPluginHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        lock (_gate) _hostContext = hostContext;
    }

    public void Detach()
    {
        lock (_gate) _hostContext = null;
    }

    public PersistedPluginSettings Load()
    {
        lock (_gate)
        {
            try
            {
                PluginSettingsDocument? document = _hostContext?.Settings.LoadAsync()
                    .AsTask().GetAwaiter().GetResult();
                if (document is not null)
                {
                    PersistedPluginSettings? persisted = JsonSerializer.Deserialize<PersistedPluginSettings>(document.Json);
                    if (persisted is not null) return Normalize(persisted);
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

            return CreateDefaults();
        }
    }

    public void Save(PersistedPluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_hostContext is null) return;

            try
            {
                string json = JsonSerializer.Serialize(Normalize(settings));
                _hostContext.Settings.SaveAsync(new PluginSettingsDocument(1, json))
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _hostContext.Logger.Log(
                    PluginLogLevel.Warning,
                    "settings.save-failed",
                    "Meshtastic plugin settings could not be saved; the previous persisted value will be retained.",
                    exception);
            }
        }
    }

    private static PersistedPluginSettings CreateDefaults() => Normalize(new());

    private static PersistedPluginSettings Normalize(PersistedPluginSettings settings)
    {
        MeshtasticRegion region = Enum.IsDefined(settings.Region) ? settings.Region : MeshtasticRegion.JP;
        MeshtasticModemPreset preset = Enum.IsDefined(settings.ModemPreset)
            ? settings.ModemPreset
            : MeshtasticModemPreset.LongFast;
        MeshtasticModemPreset lastSpecifiedPreset =
            Enum.IsDefined(settings.LastSpecifiedModemPreset) &&
            !MeshtasticJpLongFastProfile.IsAutoSf(settings.LastSpecifiedModemPreset)
                ? settings.LastSpecifiedModemPreset
                : MeshtasticModemPreset.LongFast;

        MeshtasticRegionProfile regionProfile = MeshtasticJpLongFastProfile.GetRegion(region);
        MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(preset);
        int maximumChannel = Math.Max(1, regionProfile.GetMaximumChannel(profile.BandwidthHz));
        int channel = settings.RadioChannel <= 0
            ? Math.Clamp(MeshtasticJpLongFastProfile.DefaultChannel, 1, maximumChannel)
            : Math.Clamp(settings.RadioChannel, 1, maximumChannel);
        string fallbackChannel = channel.ToString(CultureInfo.InvariantCulture);

        return settings with
        {
            Region = region,
            ModemPreset = preset,
            RadioChannel = channel,
            RadioChannels250 = NormalizeChannels(settings.RadioChannels250, regionProfile.GetMaximumChannel(250_000), fallbackChannel),
            RadioChannels125 = NormalizeChannels(settings.RadioChannels125, regionProfile.GetMaximumChannel(125_000), fallbackChannel),
            HistoryDisplayLimit = Math.Clamp(settings.HistoryDisplayLimit, MinimumHistoryDisplayLimit, MaximumHistoryDisplayLimit),
            HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, MinimumHistoryRetentionDays, MaximumHistoryRetentionDays),
            LastSpecifiedModemPreset = lastSpecifiedPreset
        };
    }

    private static string NormalizeChannels(string? text, int maximumChannel, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text) || maximumChannel < 1) return fallback;
        var channels = new System.Collections.Generic.SortedSet<int>();
        foreach (string part in text.Split([',', ' ', '\u3001'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel) ||
                channel < 1 || channel > maximumChannel)
                return fallback;
            channels.Add(channel);
        }

        return channels.Count == 0
            ? fallback
            : string.Join(',', channels.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

}
