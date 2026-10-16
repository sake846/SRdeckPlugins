using System.IO;
using System.Text.Json;
using SRdeckPlugin.Meshtastic.Services;

namespace SRdeckPlugin.Meshtastic.Protocols;

internal static class MeshtasticHardwareCatalog
{
    private const string FileName = "MeshtasticHardwareModels.json";
    private const string ResourceName = "SRdeckPlugin.Meshtastic.MeshtasticHardwareModels.json";
    private static readonly object Gate = new();
    private static string? _configuredDataDirectory;
    private static Lazy<IReadOnlyDictionary<uint, string>> ProductNames =
        new(() => LoadProductNames(null));
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> DefaultProductNames = new(() =>
    {
        using Stream stream = typeof(MeshtasticHardwareCatalog).Assembly.GetManifestResourceStream(ResourceName)!;
        return ReadProductNames(stream);
    });

    public static void ConfigureDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string fullPath = Path.GetFullPath(dataDirectory);
        lock (Gate)
        {
            if (string.Equals(_configuredDataDirectory, fullPath, StringComparison.OrdinalIgnoreCase))
                return;

            _configuredDataDirectory = fullPath;
            ProductNames = new Lazy<IReadOnlyDictionary<uint, string>>(
                () => LoadProductNames(fullPath));
        }

        EnsureCatalogFile(Path.Combine(fullPath, FileName));
    }

    public static bool TryGetProductName(uint hardwareId, out string productName)
    {
        Lazy<IReadOnlyDictionary<uint, string>> catalog;
        lock (Gate)
            catalog = ProductNames;

        if (catalog.Value.TryGetValue(hardwareId, out string? candidate) &&
            !string.IsNullOrWhiteSpace(candidate))
        {
            productName = candidate;
            return true;
        }

        productName = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<uint, string> LoadProductNames(string? configuredDataDirectory)
    {
        string dataDirectory = configuredDataDirectory ?? MeshtasticDataPaths.DefaultDataDirectory;
        string path = Path.Combine(dataDirectory, FileName);
        try
        {
            EnsureCatalogFile(path);
            using FileStream stream = File.OpenRead(path);
            return ReadProductNames(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<uint, string>();
        }
    }

    public static string GetDefaultProductName(uint hardwareId) =>
        DefaultProductNames.Value.TryGetValue(hardwareId, out string? name)
            ? name : $"不明なハードウェア ({hardwareId})";

    private static IReadOnlyDictionary<uint, string> ReadProductNames(Stream stream)
    {
        HardwareCatalogFile? catalog = JsonSerializer.Deserialize<HardwareCatalogFile>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Dictionary<uint, string> productNames = [];
        foreach (HardwareModelDefinition model in catalog?.Models ?? [])
        {
            if (!string.IsNullOrWhiteSpace(model.ProductName))
                productNames[model.Id] = model.ProductName.Trim();
        }
        return productNames;
    }

    private static void EnsureCatalogFile(string path)
    {
        if (File.Exists(path)) return;

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using Stream? source = typeof(MeshtasticHardwareCatalog).Assembly
                .GetManifestResourceStream(ResourceName);
            if (source is null) return;

            using FileStream destination = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            source.CopyTo(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The built-in catalog remains available if AppData cannot be written.
        }
    }

    private sealed class HardwareCatalogFile
    {
        public int SchemaVersion { get; init; }
        public List<HardwareModelDefinition> Models { get; init; } = [];
    }

    private sealed class HardwareModelDefinition
    {
        public uint Id { get; init; }
        public string ProductName { get; init; } = string.Empty;
    }
}
