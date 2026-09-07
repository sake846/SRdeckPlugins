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
        $"ID={Id ?? "-"}  ノード名={LongName ?? "-"}  短縮名={ShortName ?? "-"}  " +
        $"MAC={MacAddress ?? "-"}  ハードウェア={HardwareName}({HardwareModel})  役割={RoleName}({Role})  " +
        $"免許局={(IsLicensed ? "有" : "無")}  メッセージ不可={(IsUnmessagable == true ? "はい" : IsUnmessagable == false ? "いいえ" : "-")}  公開鍵={PublicKey ?? "-"}";
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
        $"緯度={(Latitude?.ToString("F7", CultureInfo.InvariantCulture) ?? "-")}  " +
        $"経度={(Longitude?.ToString("F7", CultureInfo.InvariantCulture) ?? "-")}  " +
        $"高度={(AltitudeMeters.HasValue ? $"{AltitudeMeters.Value} m" : "-")}  " +
        $"測位時刻={(Time?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")}  " +
        $"位置情報源={GetLocationSource(LocationSource)}  高度情報源={GetAltitudeSource(AltitudeSource)}  " +
        $"PDOP={FormatDop(Pdop)}  HDOP={FormatDop(Hdop)}  VDOP={FormatDop(Vdop)}  " +
        $"GPS精度={(GpsAccuracyMillimeters.HasValue ? $"{GpsAccuracyMillimeters.Value / 1000.0:F2} m" : "-")}  " +
        $"対地速度={(GroundSpeed.HasValue ? $"{GroundSpeed.Value} m/s" : "-")}  対地進行方向={FormatTrack(GroundTrack)}  " +
        $"測位品質={FixQuality?.ToString() ?? "-"}  測位種別={FixType?.ToString() ?? "-"}  " +
        $"衛星数={SatellitesInView?.ToString() ?? "-"}  精度ビット数={PrecisionBits?.ToString() ?? "-"}";

    private static string FormatDop(uint? value) => value.HasValue
        ? (value.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture)
        : "-";
    private static string FormatTrack(uint? value) => value.HasValue
        ? (value.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture)
        : "-";
    private static string GetLocationSource(uint value) => value switch
    {
        1 => "手動", 2 => "内蔵", 3 => "外部", _ => "未設定"
    };
    private static string GetAltitudeSource(uint value) => value switch
    {
        1 => "手動", 2 => "内蔵", 3 => "外部", 4 => "気圧計", _ => "未設定"
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
        $"測定時刻={(Time?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")}  種別={Variant}  {string.Join("  ", Metrics.Select(FormatTelemetryMetricJapanese))}".TrimEnd();

    private static string FormatTelemetryMetricJapanese(string metric)
    {
        int eqIndex = metric.IndexOf('=');
        if (eqIndex <= 0) return metric;
        string key = metric[..eqIndex];
        string value = metric[(eqIndex + 1)..];
        string jpKey = key switch
        {
            "battery" => "バッテリー",
            "voltage" => "電圧",
            "channelUtilization" => "チャネル利用率",
            "airUtilTx" => "送信利用率",
            "uptime" => "稼働時間",
            "temperature" => "温度",
            "humidity" => "湿度",
            "pressure" => "気圧",
            "gasResistance" => "ガス抵抗",
            "current" => "電流",
            "iaq" => "空気質指数",
            "distance" => "距離",
            "lux" => "照度",
            "whiteLux" => "白色照度",
            "irLux" => "赤外照度",
            "uvLux" => "UV照度",
            "windDirection" => "風向",
            "windSpeed" => "風速",
            "weight" => "重量",
            "windGust" => "最大瞬間風速",
            "windLull" => "最小風速",
            "radiation" => "放射線量",
            "rainfall1h" => "1時間雨量",
            "rainfall24h" => "24時間雨量",
            "soilMoisture" => "土壌水分",
            "soilTemperature" => "土壌温度",
            "oneWireTemperature" => "1-Wire温度",
            "packetsTx" => "送信パケット数",
            "packetsRx" => "受信パケット数",
            "packetsRxBad" => "エラーパケット数",
            "onlineNodes" => "オンラインノード数",
            "totalNodes" => "総ノード数",
            "rxDuplicates" => "重複受信数",
            "txRelay" => "中継送信数",
            "txRelayCanceled" => "中継取消数",
            "heapTotal" => "総ヒープ",
            "heapFree" => "空きヒープ",
            "txDropped" => "破棄送信数",
            "noiseFloor" => "ノイズフロア",
            _ => key
        };
        return $"{jpKey}={value}";
    }
}

public sealed record MeshtasticStructuredPayload(string PayloadType, string PayloadSummary, string PayloadDetails)
    : MeshtasticApplicationPayload
{
    public override string Type => PayloadType;
    public override string Summary => PayloadSummary;
    public override string Details => PayloadDetails;
}

public sealed record MeshtasticWaypoint(uint Id, double Latitude, double Longitude, uint Expire,
    uint LockedTo, string? Name, string? Description, uint Icon) : MeshtasticApplicationPayload
{
    public override string Type => "Waypoint";
    public override string Summary => $"Waypoint: {Name ?? "(名称なし)"} / {Latitude:F6}, {Longitude:F6}";
    public override string Details =>
        $"ID={Id}  緯度={Latitude:F7}  経度={Longitude:F7}  有効期限={Expire}  ロック先={LockedTo}  " +
        $"名前={Name ?? "-"}  説明={Description ?? "-"}  アイコン=U+{Icon:X}";
}

public sealed record MeshtasticNodeStatus(string? Status) : MeshtasticApplicationPayload
{
    public override string Type => "NodeStatus";
    public override string Summary => string.IsNullOrWhiteSpace(Status) ? "(ステータスなし)" : Status;
    public override string Details => $"ステータス={Status ?? "-"}";
}

public sealed record MeshtasticMapReport(string? LongName, string? ShortName, uint Role,
    MeshtasticPosition? Position, string? FirmwareVersion, uint Region, uint ModemPreset,
    bool HasDefaultChannel, uint OnlineLocalNodes) : MeshtasticApplicationPayload
{
    public override string Type => "MapReport";
    public override string Summary =>
        $"Map report: {LongName ?? "(名称なし)"}" +
        (Position?.Latitude is double lat && Position.Longitude is double lon ? $" / {lat:F6}, {lon:F6}" : "");
    public override string Details =>
        $"MAP_REPORT_APP由来  長い名前={LongName ?? "-"}  短い名前={ShortName ?? "-"}  " +
        $"役割={MeshtasticApplicationPayloadParser.GetRoleName(Role)}({Role})  位置=[{Position?.Summary ?? "-"}]  " +
        $"FW={FirmwareVersion ?? "-"}  リージョン={Region}  モデムプリセット={ModemPreset}  " +
        $"既定チャネル={(HasDefaultChannel ? "有" : "無")}  ローカルオンラインノード={OnlineLocalNodes}";
}
