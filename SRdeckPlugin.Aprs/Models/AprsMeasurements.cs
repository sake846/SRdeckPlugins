namespace SRdeckPlugin.Aprs.Models;

public sealed record AprsMeasurement(string Key, string Name, string Unit, double Value)
{
    public string Label => string.IsNullOrEmpty(Unit) ? Name : $"{Name} ({Unit})";
}

public sealed record AprsGraphPoint(DateTimeOffset ReceivedAt, double Value);

public static class AprsMeasurements
{
    public static IReadOnlyList<AprsMeasurement> From(AprsPacket packet)
    {
        var values = new List<AprsMeasurement>();
        Add("latitude", "緯度", "°", packet.Latitude);
        Add("longitude", "経度", "°", packet.Longitude);
        Add("speed", "移動速度", "kn", packet.SpeedKnots);
        Add("course", "進行方向", "°", packet.CourseDegrees);
        if (packet.Weather is { } w)
        {
            Add("temperature", "気温", "°C", w.TemperatureCelsius);
            Add("humidity", "湿度", "%", w.HumidityPercent);
            Add("pressure", "気圧", "hPa", w.PressureHectopascals);
            Add("windDirection", "風向", "°", w.WindDirectionDegrees);
            Add("windSpeed", "風速", "m/s", w.WindSpeedMetersPerSecond);
            Add("gust", "最大瞬間風速", "m/s", w.GustMetersPerSecond);
            Add("rainHour", "雨量（1時間）", "mm", w.RainLastHourMillimeters);
            Add("rain24Hours", "雨量（24時間）", "mm", w.RainLast24HoursMillimeters);
            Add("rainMidnight", "雨量（午前0時以降）", "mm", w.RainSinceMidnightMillimeters);
            Add("luminosity", "日射量", "W/m²", w.LuminosityWattsPerSquareMeter);
        }
        if (packet.Telemetry is { } t)
        {
            for (int i = 0; i < Math.Min(5, t.Analog.Length); i++)
                Add($"analog{i + 1}", $"テレメトリ A{i + 1}", "raw", t.Analog[i]);
            if (t.Digital is { Length: 8 } digital)
                for (int i = 0; i < 8; i++)
                    if (digital[i] is '0' or '1') Add($"digital{i + 1}", $"テレメトリ B{i + 1}", "bit", digital[i] - '0');
        }
        return values;

        void Add(string key, string name, string unit, double? value)
        {
            if (value is { } number && double.IsFinite(number)) values.Add(new(key, name, unit, number));
        }
    }
}
