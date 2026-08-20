using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SRdeckPlugin.Ft8.Models;

namespace SRdeckPlugin.Ft8;

internal static class Ft8BandCatalog
{
    internal const string FileName = "SRdeckPlugin.Ft8.bands.json";
    private const string EmbeddedResourceName = "SRdeckPlugin.Ft8.Bands.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    static Ft8BandCatalog()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
        (Bands, LoadWarning) = Load();
    }

    internal static IReadOnlyList<Ft8Band> Bands { get; }
    internal static string? LoadWarning { get; }

    private static (IReadOnlyList<Ft8Band> Bands, string? Warning) Load()
    {
        Assembly assembly = typeof(Ft8BandCatalog).Assembly;
        string assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        string externalPath = Path.Combine(assemblyDirectory, FileName);

        if (File.Exists(externalPath))
        {
            try
            {
                return (ParseAndValidate(File.ReadAllText(externalPath), externalPath), null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
                return (LoadEmbedded(assembly),
                    $"Band list '{externalPath}' could not be loaded; embedded defaults are used. " +
                    exception.Message);
            }
        }

        return (LoadEmbedded(assembly),
            $"Band list '{externalPath}' was not found; embedded defaults are used.");
    }

    private static IReadOnlyList<Ft8Band> LoadEmbedded(Assembly assembly)
    {
        using Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName) ??
            throw new InvalidOperationException($"Embedded band list '{EmbeddedResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return ParseAndValidate(reader.ReadToEnd(), EmbeddedResourceName);
    }

    private static IReadOnlyList<Ft8Band> ParseAndValidate(string json, string source)
    {
        BandCatalogDocument document = JsonSerializer.Deserialize<BandCatalogDocument>(json, JsonOptions) ??
            throw new InvalidDataException($"Band list '{source}' is empty.");
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Band list '{source}' has unsupported schemaVersion {document.SchemaVersion}.");
        if (document.Bands is null || document.Bands.Length == 0)
            throw new InvalidDataException($"Band list '{source}' does not contain any bands.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Ft8Band band in document.Bands)
        {
            if (string.IsNullOrWhiteSpace(band.Id) || string.IsNullOrWhiteSpace(band.Band))
                throw new InvalidDataException($"Band list '{source}' contains an entry without id or band name.");
            if (!ids.Add(band.Id))
                throw new InvalidDataException($"Band list '{source}' contains duplicate id '{band.Id}'.");
            if (band.DialFrequencyHz <= 0)
                throw new InvalidDataException($"Band '{band.Id}' has an invalid dialFrequencyHz.");
            if (!Enum.IsDefined(band.Mode))
                throw new InvalidDataException($"Band '{band.Id}' has an invalid mode.");
        }

        if (!ids.Contains(Ft8PluginModule.DefaultBandId))
            throw new InvalidDataException(
                $"Band list '{source}' must contain the default id '{Ft8PluginModule.DefaultBandId}'.");

        return Array.AsReadOnly(document.Bands);
    }

    private sealed class BandCatalogDocument
    {
        public int SchemaVersion { get; init; }
        public Ft8Band[]? Bands { get; init; }
    }
}
