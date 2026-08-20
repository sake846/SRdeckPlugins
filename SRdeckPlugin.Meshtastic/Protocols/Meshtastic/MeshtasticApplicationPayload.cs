using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

public abstract record MeshtasticApplicationPayload
{
    public abstract string Type { get; }
    public abstract string Summary { get; }
    public abstract string Details { get; }
}

public sealed record MeshtasticNodeInfo(
    string? Id,
    string? LongName,
    string? ShortName,
    string? MacAddress,
    uint HardwareModel,
    bool IsLicensed,
    uint Role,
    string? PublicKey,
    bool? IsUnmessagable) : MeshtasticApplicationPayload
{
    public override string Type => "NodeInfo";
    public string HardwareName => MeshtasticApplicationPayloadParser.GetHardwareName(HardwareModel);
    public string RoleName => MeshtasticApplicationPayloadParser.GetRoleName(Role);
    public override string Summary =>
        $"{LongName ?? Id ?? "(名称なし)"}" +
        (string.IsNullOrWhiteSpace(ShortName) ? "" : $" [{ShortName}]") +
        $" / {HardwareName} / {RoleName}";
    public override string Details =>
        $"id={Id ?? "-"} longName={LongName ?? "-"} shortName={ShortName ?? "-"} " +
        $"mac={MacAddress ?? "-"} hardware={HardwareName}({HardwareModel}) role={RoleName}({Role}) " +
        $"licensed={IsLicensed} unmessagable={(IsUnmessagable?.ToString() ?? "-")} publicKey={PublicKey ?? "-"}";
}

public sealed record MeshtasticPosition(
    double? Latitude,
    double? Longitude,
    int? AltitudeMeters,
    DateTimeOffset? Time,
    uint LocationSource,
    uint AltitudeSource,
    uint? Pdop,
    uint? Hdop,
    uint? Vdop,
    uint? GpsAccuracyMillimeters,
    uint? GroundSpeed,
    uint? GroundTrack,
    uint? FixQuality,
    uint? FixType,
    uint? SatellitesInView,
    uint? PrecisionBits) : MeshtasticApplicationPayload
{
    public override string Type => "Position";
    public override string Summary
    {
        get
        {
            string coordinates = Latitude.HasValue && Longitude.HasValue
                ? $"{Latitude.Value:F6}, {Longitude.Value:F6}"
                : "位置未設定";
            string altitude = AltitudeMeters.HasValue ? $" / {AltitudeMeters.Value} m" : "";
            string satellites = SatellitesInView.HasValue ? $" / Sat {SatellitesInView.Value}" : "";
            return coordinates + altitude + satellites;
        }
    }
    public override string Details =>
        $"latitude={(Latitude?.ToString("F7", CultureInfo.InvariantCulture) ?? "-")} " +
        $"longitude={(Longitude?.ToString("F7", CultureInfo.InvariantCulture) ?? "-")} " +
        $"altitudeM={(AltitudeMeters?.ToString(CultureInfo.InvariantCulture) ?? "-")} " +
        $"time={(Time?.ToString("O") ?? "-")} locationSource={GetLocationSource(LocationSource)} " +
        $"altitudeSource={GetAltitudeSource(AltitudeSource)} pdop={FormatDop(Pdop)} hdop={FormatDop(Hdop)} " +
        $"vdop={FormatDop(Vdop)} gpsAccuracyMm={GpsAccuracyMillimeters?.ToString() ?? "-"} " +
        $"groundSpeed={GroundSpeed?.ToString() ?? "-"} groundTrackDeg={FormatTrack(GroundTrack)} " +
        $"fixQuality={FixQuality?.ToString() ?? "-"} fixType={FixType?.ToString() ?? "-"} " +
        $"satellites={SatellitesInView?.ToString() ?? "-"} precisionBits={PrecisionBits?.ToString() ?? "-"}";

    private static string FormatDop(uint? value) => value.HasValue
        ? (value.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture)
        : "-";
    private static string FormatTrack(uint? value) => value.HasValue
        ? (value.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture)
        : "-";
    private static string GetLocationSource(uint value) => value switch
    {
        1 => "MANUAL", 2 => "INTERNAL", 3 => "EXTERNAL", _ => "UNSET"
    };
    private static string GetAltitudeSource(uint value) => value switch
    {
        1 => "MANUAL", 2 => "INTERNAL", 3 => "EXTERNAL", 4 => "BAROMETRIC", _ => "UNSET"
    };
}

public sealed record MeshtasticTelemetry(
    DateTimeOffset? Time,
    string Variant,
    IReadOnlyList<string> Metrics) : MeshtasticApplicationPayload
{
    public override string Type => "Telemetry";
    public override string Summary => Metrics.Count == 0
        ? Variant
        : $"{Variant}: {string.Join(" / ", Metrics.Take(5))}";
    public override string Details =>
        $"time={(Time?.ToString("O") ?? "-")} variant={Variant} {string.Join(" ", Metrics)}".TrimEnd();
}

public sealed record MeshtasticStructuredPayload(string PayloadType, string PayloadSummary, string PayloadDetails)
    : MeshtasticApplicationPayload
{
    public override string Type => PayloadType;
    public override string Summary => PayloadSummary;
    public override string Details => PayloadDetails;
}
