using SRdeckPlugin.AdsB.Models;

namespace SRdeckPlugin.AdsB.Protocols;

public sealed class CprDecoder
{
    private static readonly TimeSpan GlobalPairMaximumAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LocalReferenceMaximumAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StateRetention = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, Pair> pairs = new(StringComparer.Ordinal);
    private int additionsSincePrune;
    private double referenceLatitude;
    private double referenceLongitude;
    private int referenceCount;
    private double? receiverLatitude;
    private double? receiverLongitude;

    public void ConfigureReceiverReference(double? latitude, double? longitude)
    {
        receiverLatitude = latitude is >= -90 and <= 90 ? latitude : null;
        receiverLongitude = longitude is >= -180 and <= 180 ? longitude : null;
    }

    public AdsBPosition? Add(string icao, bool odd, int latitude, int longitude, DateTimeOffset receivedAt)
    {
        pairs.TryGetValue(icao, out Pair pair);
        var value = new CprFrame(latitude / 131072.0, longitude / 131072.0, receivedAt);
        pair = odd ? pair with { Odd = value } : pair with { Even = value };
        pairs[icao] = pair;
        if (++additionsSincePrune >= 256)
        {
            Prune(receivedAt);
            additionsSincePrune = 0;
        }

        AdsBPosition? global = TryDecodeGlobal(pair);
        if (global is not null && IsPlausible(pair.Position, global))
        {
            pairs[icao] = pair with { Position = global };
            UpdateReference(global);
            return global;
        }

        AdsBPosition? local = TryDecodeLocal(value, odd, pair.Position);
        if (local is null || !IsPlausible(pair.Position, local)) return null;
        pairs[icao] = pair with { Position = local };
        return local;
    }

    public AdsBPosition? AddSurface(string icao, bool odd, int latitude, int longitude,
        DateTimeOffset receivedAt)
    {
        pairs.TryGetValue(icao, out Pair pair);
        var frame = new CprFrame(latitude / 131072.0, longitude / 131072.0, receivedAt);
        AdsBPosition? reference = pair.Position is not null &&
            receivedAt - pair.Position.ReceivedAt <= LocalReferenceMaximumAge
            ? pair.Position
            : receiverLatitude is not null && receiverLongitude is not null
                ? new(receiverLatitude.Value, receiverLongitude.Value, receivedAt)
                : referenceCount > 0 ? new(referenceLatitude, referenceLongitude, receivedAt) : null;
        if (reference is null) return null;
        AdsBPosition? position = TryDecodeSurface(frame, odd, reference);
        if (position is null || !IsPlausible(pair.Position, position)) return null;
        pair = odd ? pair with { Odd = frame, Position = position } :
            pair with { Even = frame, Position = position };
        pairs[icao] = pair;
        return position;
    }

    private static AdsBPosition? TryDecodeGlobal(Pair pair)
    {
        if (pair.Even is null || pair.Odd is null ||
            (pair.Even.ReceivedAt - pair.Odd.ReceivedAt).Duration() > GlobalPairMaximumAge) return null;

        double j = Math.Floor(59 * pair.Even.Latitude - 60 * pair.Odd.Latitude + 0.5);
        double evenLatitude = 6 * (Modulo(j, 60) + pair.Even.Latitude);
        double oddLatitude = (360.0 / 59) * (Modulo(j, 59) + pair.Odd.Latitude);
        if (evenLatitude >= 270) evenLatitude -= 360;
        if (oddLatitude >= 270) oddLatitude -= 360;
        if (Nl(evenLatitude) != Nl(oddLatitude)) return null;

        bool useOdd = pair.Odd.ReceivedAt >= pair.Even.ReceivedAt;
        double latitudeResult = useOdd ? oddLatitude : evenLatitude;
        int nl = Nl(latitudeResult);
        int ni = Math.Max(nl - (useOdd ? 1 : 0), 1);
        double m = Math.Floor(pair.Even.Longitude * (nl - 1) - pair.Odd.Longitude * nl + 0.5);
        double longitudeResult = 360.0 / ni * (Modulo(m, ni) + (useOdd ? pair.Odd.Longitude : pair.Even.Longitude));
        if (longitudeResult > 180) longitudeResult -= 360;
        return new(latitudeResult, longitudeResult, useOdd ? pair.Odd.ReceivedAt : pair.Even.ReceivedAt);
    }

    private static AdsBPosition? TryDecodeLocal(CprFrame frame, bool odd, AdsBPosition? reference)
    {
        if (reference is null || frame.ReceivedAt - reference.ReceivedAt > LocalReferenceMaximumAge ||
            frame.ReceivedAt < reference.ReceivedAt) return null;
        double latitudeZone = 360.0 / (odd ? 59 : 60);
        double latitudeIndex = Math.Floor(reference.Latitude / latitudeZone) +
            Math.Floor(0.5 + Modulo(reference.Latitude, latitudeZone) / latitudeZone - frame.Latitude);
        double decodedLatitude = latitudeZone * (latitudeIndex + frame.Latitude);
        if (decodedLatitude >= 270) decodedLatitude -= 360;
        if (decodedLatitude is < -90 or > 90) return null;

        int ni = Math.Max(Nl(decodedLatitude) - (odd ? 1 : 0), 1);
        double longitudeZone = 360.0 / ni;
        double longitudeIndex = Math.Floor(reference.Longitude / longitudeZone) +
            Math.Floor(0.5 + Modulo(reference.Longitude, longitudeZone) / longitudeZone - frame.Longitude);
        double decodedLongitude = longitudeZone * (longitudeIndex + frame.Longitude);
        if (decodedLongitude > 180) decodedLongitude -= 360;
        if (decodedLongitude < -180) decodedLongitude += 360;
        return new(decodedLatitude, decodedLongitude, frame.ReceivedAt);
    }

    private static AdsBPosition? TryDecodeSurface(CprFrame frame, bool odd, AdsBPosition reference)
    {
        double latitudeZone = 90.0 / (odd ? 59 : 60);
        double latitudeIndex = Math.Floor(reference.Latitude / latitudeZone) +
            Math.Floor(0.5 + Modulo(reference.Latitude, latitudeZone) / latitudeZone - frame.Latitude);
        double latitude = latitudeZone * (latitudeIndex + frame.Latitude);
        while (latitude > reference.Latitude + 45) latitude -= 90;
        while (latitude < reference.Latitude - 45) latitude += 90;
        if (latitude is < -90 or > 90) return null;

        int ni = Math.Max(Nl(latitude) - (odd ? 1 : 0), 1);
        double longitudeZone = 90.0 / ni;
        double longitudeIndex = Math.Floor(reference.Longitude / longitudeZone) +
            Math.Floor(0.5 + Modulo(reference.Longitude, longitudeZone) / longitudeZone - frame.Longitude);
        double longitude = longitudeZone * (longitudeIndex + frame.Longitude);
        while (longitude > reference.Longitude + 45) longitude -= 90;
        while (longitude < reference.Longitude - 45) longitude += 90;
        if (longitude > 180) longitude -= 360;
        if (longitude < -180) longitude += 360;
        return new(latitude, longitude, frame.ReceivedAt);
    }

    private void UpdateReference(AdsBPosition position)
    {
        if (referenceCount == 0)
        {
            referenceLatitude = position.Latitude;
            referenceLongitude = position.Longitude;
            referenceCount = 1;
            return;
        }
        int nextCount = Math.Min(referenceCount + 1, 10_000);
        double weight = 1.0 / nextCount;
        referenceLatitude += (position.Latitude - referenceLatitude) * weight;
        double longitudeDelta = position.Longitude - referenceLongitude;
        if (longitudeDelta > 180) longitudeDelta -= 360;
        if (longitudeDelta < -180) longitudeDelta += 360;
        referenceLongitude += longitudeDelta * weight;
        if (referenceLongitude > 180) referenceLongitude -= 360;
        if (referenceLongitude < -180) referenceLongitude += 360;
        referenceCount = nextCount;
    }

    private static bool IsPlausible(AdsBPosition? previous, AdsBPosition current)
    {
        if (!double.IsFinite(current.Latitude) || !double.IsFinite(current.Longitude) ||
            current.Latitude is < -90 or > 90 || current.Longitude is < -180 or > 180) return false;
        if (previous is null) return true;
        double seconds = (current.ReceivedAt - previous.ReceivedAt).TotalSeconds;
        if (seconds < 0) return false;
        double latitudeDistanceNm = (current.Latitude - previous.Latitude) * 60;
        double meanLatitude = (current.Latitude + previous.Latitude) * Math.PI / 360;
        double longitudeDistanceNm = (current.Longitude - previous.Longitude) * 60 * Math.Cos(meanLatitude);
        double distanceNm = Math.Sqrt(latitudeDistanceNm * latitudeDistanceNm +
            longitudeDistanceNm * longitudeDistanceNm);
        return distanceNm <= 5 + seconds * (1_500.0 / 3_600);
    }

    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - StateRetention;
        foreach (string key in pairs.Where(item => item.Value.LatestReceivedAt < cutoff)
                     .Select(item => item.Key).ToArray())
            pairs.Remove(key);
    }

    public void Reset()
    {
        pairs.Clear();
        additionsSincePrune = 0;
        referenceLatitude = referenceLongitude = 0;
        referenceCount = 0;
    }

    private static double Modulo(double value, double modulus) => value - modulus * Math.Floor(value / modulus);

    private static int Nl(double latitude)
    {
        latitude = Math.Abs(latitude);
        if (latitude >= 87) return latitude > 87 ? 1 : 2;
        double a = 1 - Math.Cos(Math.PI / 30);
        double b = Math.Cos(latitude * Math.PI / 180);
        return (int)Math.Floor(2 * Math.PI / Math.Acos(1 - a / (b * b)));
    }

    private sealed record CprFrame(double Latitude, double Longitude, DateTimeOffset ReceivedAt);
    private readonly record struct Pair(CprFrame? Even, CprFrame? Odd, AdsBPosition? Position)
    {
        public DateTimeOffset LatestReceivedAt =>
            (Even?.ReceivedAt ?? default) >= (Odd?.ReceivedAt ?? default)
                ? Even?.ReceivedAt ?? default
                : Odd?.ReceivedAt ?? default;
    }
}
