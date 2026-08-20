namespace SRdeckPlugin.Ft8.Models;

/// <summary>Converts 4- or 6-character Maidenhead locators to their cell centre.</summary>
public static class MaidenheadLocator
{
    public static bool TryGetCentre(string? value, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string locator = value.Trim().ToUpperInvariant();
        if (locator.Length is not (4 or 6) ||
            locator[0] is < 'A' or > 'R' || locator[1] is < 'A' or > 'R' ||
            locator[2] is < '0' or > '9' || locator[3] is < '0' or > '9')
            return false;

        longitude = -180 + (locator[0] - 'A') * 20 + (locator[2] - '0') * 2 + 1;
        latitude = -90 + (locator[1] - 'A') * 10 + (locator[3] - '0') + 0.5;
        if (locator.Length == 4) return true;

        if (locator[4] is < 'A' or > 'X' || locator[5] is < 'A' or > 'X')
            return false;

        const double longitudeSubsquare = 2d / 24d;
        const double latitudeSubsquare = 1d / 24d;
        longitude += (locator[4] - 'A') * longitudeSubsquare +
                     longitudeSubsquare / 2 - 1;
        latitude += (locator[5] - 'A') * latitudeSubsquare +
                    latitudeSubsquare / 2 - 0.5;
        return true;
    }
}
