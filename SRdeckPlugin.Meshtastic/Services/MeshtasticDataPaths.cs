using System.IO;

namespace SRdeckPlugin.Meshtastic.Services;

internal static class MeshtasticDataPaths
{
    public static string DefaultDataDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(appData)
                ? Path.Combine(AppContext.BaseDirectory, "SRdeck", "plugins", "meshtastic")
                : Path.Combine(appData, "SRdeck", "plugins", "meshtastic");
        }
    }
}
