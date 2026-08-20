using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

public static class MeshtasticApplicationPayloadParser
{
    public static bool TryParse(MeshtasticData data, out MeshtasticApplicationPayload? payload, out string error)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            payload = data.PortNumber switch
            {
                2 => ParseRemoteHardware(data.Payload),
                3 => ParsePosition(data.Payload),
                4 => ParseNodeInfo(data.Payload),
                5 => ParseRouting(data.Payload),
                10 => ParseDetectionSensor(data.Payload),
                65 => ParseGenericProtobuf("StoreForward", data.Payload),
                67 => ParseTelemetry(data.Payload),
                70 => ParseRouteDiscovery(data.Payload, "Traceroute"),
                71 => ParseNeighborInfo(data.Payload),
                _ => null
            };
            error = string.Empty;
            return payload is not null;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            payload = null;
            error = exception.Message;
            return false;
        }
    }

    private static MeshtasticApplicationPayload ParseDetectionSensor(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        return new MeshtasticStructuredPayload("DetectionSensor", text, $"notification={text}");
    }

    private static MeshtasticApplicationPayload ParseRemoteHardware(ReadOnlySpan<byte> bytes)
    {
        ulong type = 0, gpioValue = 0, gpioMask = 0;
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            switch (field)
            {
                case 1: type = reader.ReadVarint(wire); break;
                case 2: gpioValue = reader.ReadFixed64(wire); break;
                case 3: gpioMask = reader.ReadFixed64(wire); break;
                default: reader.Skip(wire); break;
            }
        }
        string typeName = type switch { 1 => "WRITE_GPIOS", 2 => "WATCH_GPIOS", 3 => "GPIOS_CHANGED", 4 => "READ_GPIOS", 5 => "READ_GPIOS_REPLY", _ => "UNSET" };
        return new MeshtasticStructuredPayload("RemoteHardware", $"{typeName}: value=0x{gpioValue:X} mask=0x{gpioMask:X}",
            $"type={typeName}({type}) gpioValue=0x{gpioValue:X16} gpioMask=0x{gpioMask:X16}");
    }

    private static MeshtasticApplicationPayload ParseRouting(ReadOnlySpan<byte> bytes)
    {
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (field is 1 or 2)
            {
                MeshtasticApplicationPayload route = ParseRouteDiscovery(reader.ReadBytes(wire), field == 1 ? "RouteRequest" : "RouteReply");
                return route;
            }
            if (field == 3)
            {
                ulong error = reader.ReadVarint(wire);
                string name = GetRoutingError(error);
                return new MeshtasticStructuredPayload("Routing", $"Routing {name}", $"errorReason={name}({error})");
            }
            reader.Skip(wire);
        }
        return new MeshtasticStructuredPayload("Routing", "Routing ACK", "variant=empty/ACK");
    }

    private static MeshtasticApplicationPayload ParseRouteDiscovery(ReadOnlySpan<byte> bytes, string type)
    {
        List<uint> towards = [], back = [];
        List<int> snrTowards = [], snrBack = [];
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (field is 1 or 3)
            {
                List<uint> target = field == 1 ? towards : back;
                if (wire == 5) target.Add(reader.ReadFixed32(wire));
                else if (wire == 2)
                {
                    ReadOnlySpan<byte> packed = reader.ReadBytes(wire);
                    for (int offset = 0; offset + 4 <= packed.Length; offset += 4)
                        target.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packed[offset..]));
                }
                else reader.Skip(wire);
            }
            else if (field is 2 or 4)
            {
                List<int> target = field == 2 ? snrTowards : snrBack;
                if (wire == 0) target.Add(unchecked((int)reader.ReadVarint(wire)));
                else if (wire == 2) ReadPackedVarints(reader.ReadBytes(wire), target);
                else reader.Skip(wire);
            }
            else reader.Skip(wire);
        }
        string forward = FormatRoute(towards, snrTowards);
        string reverse = FormatRoute(back, snrBack);
        return new MeshtasticStructuredPayload(type, $"往路 {forward}" + (back.Count > 0 ? $" / 復路 {reverse}" : ""),
            $"routeTowards={forward} routeBack={reverse}");
    }

    private static MeshtasticApplicationPayload ParseNeighborInfo(ReadOnlySpan<byte> bytes)
    {
        uint nodeId = 0, lastSentBy = 0, interval = 0;
        List<string> neighbors = [];
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (field == 1) nodeId = checked((uint)reader.ReadVarint(wire));
            else if (field == 2) lastSentBy = checked((uint)reader.ReadVarint(wire));
            else if (field == 3) interval = checked((uint)reader.ReadVarint(wire));
            else if (field == 4) neighbors.Add(ParseNeighbor(reader.ReadBytes(wire)));
            else reader.Skip(wire);
        }
        return new MeshtasticStructuredPayload("NeighborInfo", $"Node !{nodeId:x8}: neighbor {neighbors.Count}",
            $"nodeId=!{nodeId:x8} lastSentBy=!{lastSentBy:x8} interval={interval}s neighbors=[{string.Join(", ", neighbors)}]");
    }

    private static string ParseNeighbor(ReadOnlySpan<byte> bytes)
    {
        uint id = 0, lastRx = 0, interval = 0; float? snr = null;
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (field == 1) id = checked((uint)reader.ReadVarint(wire));
            else if (field == 2) snr = reader.ReadFloat(wire);
            else if (field == 3) lastRx = checked((uint)reader.ReadVarint(wire));
            else if (field == 4) interval = checked((uint)reader.ReadVarint(wire));
            else reader.Skip(wire);
        }
        return $"!{id:x8} snr={snr?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"}dB lastRx={lastRx} interval={interval}s";
    }

    private static MeshtasticApplicationPayload ParseGenericProtobuf(string type, ReadOnlySpan<byte> bytes)
    {
        List<string> fields = [];
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            string value = wire switch
            {
                0 => reader.ReadVarint(wire).ToString(CultureInfo.InvariantCulture),
                1 => $"0x{reader.ReadFixed64(wire):X16}",
                2 => Convert.ToHexString(reader.ReadBytes(wire)),
                5 => $"0x{reader.ReadFixed32(wire):X8}",
                _ => throw new FormatException($"unsupported protobuf wire type {wire}")
            };
            fields.Add($"field{field}={value}");
        }
        string details = string.Join(" ", fields);
        return new MeshtasticStructuredPayload(type, fields.Count == 0 ? type : $"{type}: {string.Join(" / ", fields.Take(3))}", details);
    }

    private static void ReadPackedVarints(ReadOnlySpan<byte> bytes, List<int> values)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            ulong value = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (offset >= bytes.Length || shift >= 64) throw new FormatException("invalid packed varint");
                byte current = bytes[offset++];
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) break;
            }
            values.Add(unchecked((int)value));
        }
    }

    private static string FormatRoute(List<uint> nodes, List<int> snrs) => nodes.Count == 0 ? "(empty)" :
        string.Join(" → ", nodes.Select((node, index) => $"!{node:x8}" + (index < snrs.Count ? $"({snrs[index] / 4.0:F1}dB)" : "")));

    private static string GetRoutingError(ulong value) => value switch
    {
        0 => "NONE", 1 => "NO_ROUTE", 2 => "GOT_NAK", 3 => "TIMEOUT", 4 => "NO_INTERFACE",
        5 => "MAX_RETRANSMIT", 6 => "NO_CHANNEL", 7 => "TOO_LARGE", 8 => "NO_RESPONSE",
        9 => "DUTY_CYCLE_LIMIT", 32 => "BAD_REQUEST", 33 => "NOT_AUTHORIZED", 34 => "PKI_FAILED",
        35 => "PKI_UNKNOWN_PUBKEY", 36 => "ADMIN_BAD_SESSION_KEY", 37 => "ADMIN_PUBLIC_KEY_UNAUTHORIZED",
        38 => "RATE_LIMIT_EXCEEDED", 39 => "PKI_SEND_FAIL_PUBLIC_KEY", _ => $"ERROR_{value}"
    };

    private static MeshtasticNodeInfo ParseNodeInfo(ReadOnlySpan<byte> bytes)
    {
        string? id = null, longName = null, shortName = null, mac = null, publicKey = null;
        uint hardware = 0, role = 0;
        bool licensed = false;
        bool? unmessagable = null;
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            switch (field)
            {
                case 1: id = ReadString(ref reader, wire); break;
                case 2: longName = ReadString(ref reader, wire); break;
                case 3: shortName = ReadString(ref reader, wire); break;
                case 4: mac = FormatMac(reader.ReadBytes(wire)); break;
                case 5: hardware = checked((uint)reader.ReadVarint(wire)); break;
                case 6: licensed = reader.ReadVarint(wire) != 0; break;
                case 7: role = checked((uint)reader.ReadVarint(wire)); break;
                case 8: publicKey = Convert.ToHexString(reader.ReadBytes(wire)); break;
                case 9: unmessagable = reader.ReadVarint(wire) != 0; break;
                default: reader.Skip(wire); break;
            }
        }
        return new MeshtasticNodeInfo(id, longName, shortName, mac, hardware, licensed, role, publicKey, unmessagable);
    }

    private static MeshtasticPosition ParsePosition(ReadOnlySpan<byte> bytes)
    {
        double? latitude = null, longitude = null;
        int? altitude = null;
        uint timeSeconds = 0, timestamp = 0, locationSource = 0, altitudeSource = 0;
        uint? pdop = null, hdop = null, vdop = null, accuracy = null, speed = null, track = null;
        uint? fixQuality = null, fixType = null, satellites = null, precisionBits = null;
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            switch (field)
            {
                case 1: latitude = unchecked((int)reader.ReadFixed32(wire)) * 1e-7; break;
                case 2: longitude = unchecked((int)reader.ReadFixed32(wire)) * 1e-7; break;
                case 3: altitude = unchecked((int)reader.ReadVarint(wire)); break;
                case 4: timeSeconds = reader.ReadFixed32(wire); break;
                case 5: locationSource = checked((uint)reader.ReadVarint(wire)); break;
                case 6: altitudeSource = checked((uint)reader.ReadVarint(wire)); break;
                case 7: timestamp = reader.ReadFixed32(wire); break;
                case 8: reader.ReadVarint(wire); break;
                case 9 or 10: MeshtasticProtobufReader.DecodeZigZag32(reader.ReadVarint(wire)); break;
                case 11: pdop = checked((uint)reader.ReadVarint(wire)); break;
                case 12: hdop = checked((uint)reader.ReadVarint(wire)); break;
                case 13: vdop = checked((uint)reader.ReadVarint(wire)); break;
                case 14: accuracy = checked((uint)reader.ReadVarint(wire)); break;
                case 15: speed = checked((uint)reader.ReadVarint(wire)); break;
                case 16: track = checked((uint)reader.ReadVarint(wire)); break;
                case 17: fixQuality = checked((uint)reader.ReadVarint(wire)); break;
                case 18: fixType = checked((uint)reader.ReadVarint(wire)); break;
                case 19: satellites = checked((uint)reader.ReadVarint(wire)); break;
                case 23: precisionBits = checked((uint)reader.ReadVarint(wire)); break;
                default: reader.Skip(wire); break;
            }
        }

        uint effectiveTime = timestamp != 0 ? timestamp : timeSeconds;
        DateTimeOffset? time = effectiveTime == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(effectiveTime);
        return new MeshtasticPosition(latitude, longitude, altitude, time, locationSource, altitudeSource,
            pdop, hdop, vdop, accuracy, speed, track, fixQuality, fixType, satellites, precisionBits);
    }

    private static MeshtasticTelemetry ParseTelemetry(ReadOnlySpan<byte> bytes)
    {
        uint timeSeconds = 0;
        string variant = "Unknown";
        List<string> metrics = [];
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (field == 1)
            {
                timeSeconds = reader.ReadFixed32(wire);
            }
            else if (field is >= 2 and <= 9)
            {
                variant = GetTelemetryVariant(field);
                metrics = ParseMetrics(field, reader.ReadBytes(wire));
            }
            else
            {
                reader.Skip(wire);
            }
        }

        DateTimeOffset? time = timeSeconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timeSeconds);
        return new MeshtasticTelemetry(time, variant, metrics);
    }

    private static List<string> ParseMetrics(int variant, ReadOnlySpan<byte> bytes)
    {
        var metrics = new List<string>();
        var reader = new MeshtasticProtobufReader(bytes);
        while (reader.TryReadField(out int field, out int wire))
        {
            MetricDefinition? definition = GetMetricDefinition(variant, field);
            if (definition is null)
            {
                reader.Skip(wire);
                continue;
            }

            string value = definition.Value.Kind switch
            {
                MetricKind.Float => reader.ReadFloat(wire).ToString("0.###", CultureInfo.InvariantCulture),
                MetricKind.Signed => unchecked((int)reader.ReadVarint(wire)).ToString(CultureInfo.InvariantCulture),
                _ => reader.ReadVarint(wire).ToString(CultureInfo.InvariantCulture)
            };
            metrics.Add($"{definition.Value.Name}={value}{definition.Value.Unit}");
        }
        return metrics;
    }

    private static MetricDefinition? GetMetricDefinition(int variant, int field)
    {
        if (variant == 2) return field switch
        {
            1 => U("battery", "%"), 2 => F("voltage", "V"), 3 => F("channelUtilization", "%"),
            4 => F("airUtilTx", "%"), 5 => U("uptime", "s"), _ => null
        };
        if (variant == 3) return field switch
        {
            1 => F("temperature", "°C"), 2 => F("humidity", "%"), 3 => F("pressure", "hPa"),
            4 => F("gasResistance", "MOhm"), 5 => F("voltage", "V"), 6 => F("current", "A"),
            7 => U("iaq"), 8 => F("distance", "mm"), 9 => F("lux", "lx"), 10 => F("whiteLux"),
            11 => F("irLux", "lx"), 12 => F("uvLux", "lx"), 13 => U("windDirection", "°"),
            14 => F("windSpeed", "m/s"), 15 => F("weight", "kg"), 16 => F("windGust", "m/s"),
            17 => F("windLull", "m/s"), 18 => F("radiation", "µR/h"), 19 => F("rainfall1h", "mm"),
            20 => F("rainfall24h", "mm"), 21 => U("soilMoisture", "%"),
            22 => F("soilTemperature", "°C"), 23 => F("oneWireTemperature", "°C"), _ => null
        };
        if (variant == 4) return GetAirQualityDefinition(field);
        if (variant == 5 && field is >= 1 and <= 16)
            return F($"ch{(field + 1) / 2}_{(field % 2 == 1 ? "voltage" : "current")}", field % 2 == 1 ? "V" : "A");
        if (variant == 6) return field switch
        {
            1 => U("uptime", "s"), 2 => F("channelUtilization", "%"), 3 => F("airUtilTx", "%"),
            4 => U("packetsTx"), 5 => U("packetsRx"), 6 => U("packetsRxBad"), 7 => U("onlineNodes"),
            8 => U("totalNodes"), 9 => U("rxDuplicates"), 10 => U("txRelay"),
            11 => U("txRelayCanceled"), 12 => U("heapTotal", "B"), 13 => U("heapFree", "B"),
            14 => U("txDropped"), 15 => S("noiseFloor", "dBm"), _ => null
        };
        if (variant == 7) return field switch
        {
            1 => U("heartRate", "bpm"), 2 => U("SpO2", "%"), 3 => F("temperature", "°C"), _ => null
        };
        if (variant == 8) return field switch
        {
            1 => U("uptime", "s"), 2 => U("freeMemory", "B"), 3 => U("diskFree1", "B"),
            4 => U("diskFree2", "B"), 5 => U("diskFree3", "B"), 6 => U("load1", "/100"),
            7 => U("load5", "/100"), 8 => U("load15", "/100"), _ => null
        };
        if (variant == 9) return field switch
        {
            1 => U("packetsInspected"), 2 => U("positionDedupDrops"), 3 => U("nodeInfoCacheHits"),
            4 => U("rateLimitDrops"), 5 => U("unknownPacketDrops"), 6 => U("hopExhausted"),
            7 => U("routerHopsPreserved"), _ => null
        };
        return null;
    }

    private static MetricDefinition? GetAirQualityDefinition(int field) => field switch
    {
        1 => U("PM1.0", "µg/m³"), 2 => U("PM2.5", "µg/m³"), 3 => U("PM10", "µg/m³"),
        4 => U("PM1.0env", "µg/m³"), 5 => U("PM2.5env", "µg/m³"), 6 => U("PM10env", "µg/m³"),
        7 => U("particles0.3um"), 8 => U("particles0.5um"), 9 => U("particles1.0um"),
        10 => U("particles2.5um"), 11 => U("particles5.0um"), 12 => U("particles10um"),
        13 => U("CO2", "ppm"), 14 => F("CO2Temperature", "°C"), 15 => F("CO2Humidity", "%"),
        16 => F("formaldehyde", "ppb"), 17 => F("formHumidity", "%"), 18 => F("formTemperature", "°C"),
        19 => U("PM4.0", "µg/m³"), 20 => U("particles4.0um"), 21 => F("PMTemperature", "°C"),
        22 => F("PMHumidity", "%"), 23 => F("VOCIndex"), 24 => F("NOxIndex"),
        25 => F("typicalParticleSize", "µm"), _ => null
    };

    private static string GetTelemetryVariant(int field) => field switch
    {
        2 => "DeviceMetrics", 3 => "EnvironmentMetrics", 4 => "AirQualityMetrics",
        5 => "PowerMetrics", 6 => "LocalStats", 7 => "HealthMetrics",
        8 => "HostMetrics", 9 => "TrafficManagementStats", _ => "Unknown"
    };

    private static string ReadString(ref MeshtasticProtobufReader reader, int wire) =>
        Encoding.UTF8.GetString(reader.ReadBytes(wire));

    private static string FormatMac(ReadOnlySpan<byte> bytes) =>
        string.Join(":", bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public static string GetHardwareName(uint value) => value switch
    {
        0 => "UNSET", 1 => "TLORA_V2", 2 => "TLORA_V1", 3 => "TLORA_V2_1_1P6", 4 => "TBEAM",
        5 => "HELTEC_V2_0", 6 => "TBEAM_V0P7", 7 => "T_ECHO", 8 => "TLORA_V1_1P3",
        9 => "RAK4631", 10 => "HELTEC_V2_1", 12 => "LILYGO_TBEAM_S3_CORE", 13 => "RAK11200",
        16 => "TLORA_T3_S3", 18 => "NANO_G2_ULTRA", 37 => "PORTDUINO", 38 => "ANDROID_SIM",
        43 => "HELTEC_V3", 48 => "HELTEC_WIRELESS_TRACKER", 49 => "HELTEC_WIRELESS_PAPER",
        50 => "T_DECK", 51 => "T_WATCH_S3", _ => $"HARDWARE_{value}"
    };

    public static string GetRoleName(uint value) => value switch
    {
        0 => "CLIENT", 1 => "CLIENT_MUTE", 2 => "ROUTER", 3 => "ROUTER_CLIENT", 4 => "REPEATER",
        5 => "TRACKER", 6 => "SENSOR", 7 => "TAK", 8 => "CLIENT_HIDDEN", 9 => "LOST_AND_FOUND",
        10 => "TAK_TRACKER", 11 => "ROUTER_LATE", 12 => "CLIENT_BASE", _ => $"ROLE_{value}"
    };

    private enum MetricKind { Unsigned, Signed, Float }
    private readonly record struct MetricDefinition(string Name, MetricKind Kind, string Unit);
    private static MetricDefinition U(string name, string unit = "") => new(name, MetricKind.Unsigned, unit);
    private static MetricDefinition S(string name, string unit = "") => new(name, MetricKind.Signed, unit);
    private static MetricDefinition F(string name, string unit = "") => new(name, MetricKind.Float, unit);
}
