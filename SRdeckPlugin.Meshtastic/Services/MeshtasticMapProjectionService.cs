using System;
using System.Collections.Generic;
using System.Linq;

namespace SRdeckPlugin.Meshtastic.Services;

public readonly record struct MeshtasticMapCoordinate(uint NodeNumber, double Latitude, double Longitude);

public readonly record struct MeshtasticMapProjection(uint NodeNumber, double X, double Y);

/// <summary>
/// Projects geographic coordinates into the fixed 412x94 map canvas used by
/// MeshtasticMapView. It owns no WPF state and keeps the ViewModel responsible
/// only for applying the calculated coordinates to observable points.
/// </summary>
public sealed class MeshtasticMapProjectionService
{
    public IReadOnlyList<MeshtasticMapProjection> Project(
        IEnumerable<MeshtasticMapCoordinate> coordinates)
    {
        MeshtasticMapCoordinate[] points = coordinates.ToArray();
        if (points.Length == 0) return [];

        double minLat = points.Min(point => point.Latitude);
        double maxLat = points.Max(point => point.Latitude);
        double minLon = points.Min(point => point.Longitude);
        double maxLon = points.Max(point => point.Longitude);
        double latRange = Math.Max(maxLat - minLat, 0.000001);
        double lonRange = Math.Max(maxLon - minLon, 0.000001);

        return points.Select(point => new MeshtasticMapProjection(
            point.NodeNumber,
            12 + ((point.Longitude - minLon) / lonRange * 390),
            82 - ((point.Latitude - minLat) / latRange * 68))).ToArray();
    }
}
