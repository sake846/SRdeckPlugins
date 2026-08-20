using System.Globalization;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.AdsB.Dsp;
using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.AdsB.ViewModels;
using SRdeckPlugin.AdsB.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AdsB;

public sealed partial class AdsBPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<ExportRecord> persisted = host is null
            ? []
            : PluginJsonLinesHistory.LoadAll<ExportRecord>(GetHistoryPath(host));
        ExportRecord[] records;
        if (persisted.Count > 0)
        {
            records = persisted.Where(item =>
                (request.From is null || item.ReceivedAt >= request.From) &&
                (request.To is null || item.ReceivedAt <= request.To))
                    .TakeLast(settings.MaximumHistory).ToArray();
        }
        else
        {
            lock (gate)
            {
                records = history.Where(item =>
                    (request.From is null || item.ReceivedAt >= request.From) &&
                    (request.To is null || item.ReceivedAt <= request.To)).ToArray();
            }
        }
        try
        {
            if (request.FormatId == "json")
            {
                string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(request.DestinationPath, json, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (request.FormatId == "csv")
            {
                var csv = new StringBuilder("receivedAt,icao,kind,callsign,barometricAltitudeFeet,geometricAltitudeFeet," +
                    "speedKnots,trackDegrees,verticalRate,selectedAltitudeFeet,selectedHeadingDegrees," +
                    "emergency,squawk,adsBVersion,nacP,sil,latitude,longitude,typeCode,airspeedKnots," +
                    "headingDegrees,isTrueAirspeed,selectedHeadingIsTrack,nicA,nicBaro,isOnGround,isOddCpr," +
                    "cprLatitude,cprLongitude,rawHex,streamId,samplePosition,signalQuality\r\n");
                foreach (ExportRecord item in records)
                    csv.Append(Csv(item.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                        .Append(Csv(item.Icao)).Append(',').Append(Csv(item.Kind)).Append(',').Append(Csv(item.Callsign)).Append(',')
                        .Append(Invariant(item.BarometricAltitudeFeet)).Append(',')
                        .Append(Invariant(item.GeometricAltitudeFeet)).Append(',').Append(Invariant(item.SpeedKnots)).Append(',')
                        .Append(Invariant(item.TrackDegrees)).Append(',').Append(Invariant(item.VerticalRate)).Append(',')
                        .Append(Invariant(item.SelectedAltitudeFeet)).Append(',')
                        .Append(Invariant(item.SelectedHeadingDegrees)).Append(',').Append(Csv(item.EmergencyState)).Append(',')
                        .Append(Csv(item.Squawk)).Append(',').Append(Invariant(item.AdsBVersion)).Append(',')
                        .Append(Invariant(item.NacP)).Append(',').Append(Invariant(item.Sil)).Append(',')
                        .Append(Invariant(item.Latitude)).Append(',').Append(Invariant(item.Longitude)).Append(',')
                        .Append(item.TypeCode.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Invariant(item.AirspeedKnots)).Append(',').Append(Invariant(item.HeadingDegrees)).Append(',')
                        .Append(Invariant(item.IsTrueAirspeed)).Append(',').Append(Invariant(item.SelectedHeadingIsTrack)).Append(',')
                        .Append(Invariant(item.NicA)).Append(',').Append(Invariant(item.NicBaro)).Append(',')
                        .Append(Invariant(item.IsOnGround)).Append(',').Append(Invariant(item.IsOddCpr)).Append(',')
                        .Append(Invariant(item.CprLatitude)).Append(',').Append(Invariant(item.CprLongitude)).Append(',')
                        .Append(Csv(item.RawHex)).Append(',').Append(item.StreamId.ToString("D")).Append(',')
                        .Append(item.SamplePosition.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.SignalQuality.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                await File.WriteAllTextAsync(request.DestinationPath, csv.ToString(), new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            else return new(false, 0, $"Unknown ADS-B export format '{request.FormatId}'.");
            return new(true, records.Length, $"Exported {records.Length} ADS-B messages.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host?.Logger.Log(PluginLogLevel.Error, "adsb.export.failed", "ADS-B export failed.", exception);
            return new(false, 0, exception.Message);
        }
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (processingGate)
        {
            receiver.ResetChannel();
            cprDecoder.Reset();
        }
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        if (host is not null)
        {
            host.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
            PersistSettings();
        }
        host = null;
    }

    private void PersistSettings()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            AdsBSettings settingsToPersist = settings with
            {
                MapState = GeoMapStateStore.GetState(Descriptor.Id)
            };
            context.Settings.SaveAsync(new PluginSettingsDocument(1, JsonSerializer.Serialize(settingsToPersist)))
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "adsb.settings.save-failed",
                "ADS-B settings could not be saved.", exception);
        }
    }

    private void ApplyMessage(ModeSFrame frame, AdsBMessage message)
    {
        AircraftState state;
        AdsBPosition? position = null;
        lock (gate)
        {
            if (!aircraft.TryGetValue(message.Icao, out state!))
                aircraft[message.Icao] = state = new AircraftState { Icao = message.Icao };
            if (!string.IsNullOrWhiteSpace(message.Callsign)) state.Callsign = message.Callsign;
            if (message.AltitudeFeet is not null)
            {
                state.AltitudeFeet = message.AltitudeFeet;
                if (message.IsGeometricAltitude == true) state.GeometricAltitudeFeet = message.AltitudeFeet;
                else state.BarometricAltitudeFeet = message.AltitudeFeet;
            }
            if (message.GroundSpeedKnots is not null) state.GroundSpeedKnots = message.GroundSpeedKnots;
            if (message.TrackDegrees is not null) state.TrackDegrees = message.TrackDegrees;
            if (message.AirspeedKnots is not null) state.AirspeedKnots = message.AirspeedKnots;
            if (message.HeadingDegrees is not null) state.HeadingDegrees = message.HeadingDegrees;
            if (message.VerticalRateFeetPerMinute is not null) state.VerticalRateFeetPerMinute = message.VerticalRateFeetPerMinute;
            if (message.SelectedAltitudeFeet is not null) state.SelectedAltitudeFeet = message.SelectedAltitudeFeet;
            if (message.SelectedHeadingDegrees is not null) state.SelectedHeadingDegrees = message.SelectedHeadingDegrees;
            if (message.SelectedHeadingIsTrack is not null) state.SelectedHeadingIsTrack = message.SelectedHeadingIsTrack;
            if (message.EmergencyState is not null) state.EmergencyState = message.EmergencyState;
            if (message.Squawk is not null) state.Squawk = message.Squawk;
            if (message.AdsBVersion is not null) state.AdsBVersion = message.AdsBVersion;
            if (message.NacP is not null) state.NacP = message.NacP;
            if (message.Sil is not null) state.Sil = message.Sil;
            if (message.NicA is not null) state.NicA = message.NicA;
            if (message.NicBaro is not null) state.NicBaro = message.NicBaro;
            if (message.IsOnGround is not null) state.IsOnGround = message.IsOnGround.Value;
            if (message.IsSurfacePosition) state.AltitudeFeet = 0;
            if (message.IsOddCpr is not null && message.CprLatitude is not null && message.CprLongitude is not null)
                position = message.IsSurfacePosition
                    ? cprDecoder.AddSurface(message.Icao, message.IsOddCpr.Value,
                        message.CprLatitude.Value, message.CprLongitude.Value, frame.ReceivedAt)
                    : cprDecoder.Add(message.Icao, message.IsOddCpr.Value,
                        message.CprLatitude.Value, message.CprLongitude.Value, frame.ReceivedAt);
            if (position is not null)
            {
                state.Latitude = position.Latitude;
                state.Longitude = position.Longitude;
            }
            state.LastSeen = frame.ReceivedAt;
            state.MessageCount++;
            Prune(frame.ReceivedAt);
            var record = new ExportRecord(frame.ReceivedAt, message.Icao, message.Kind, state.Callsign,
                state.BarometricAltitudeFeet, state.GeometricAltitudeFeet, state.GroundSpeedKnots,
                state.TrackDegrees, state.VerticalRateFeetPerMinute, state.SelectedAltitudeFeet,
                state.SelectedHeadingDegrees, state.EmergencyState, state.Squawk, state.AdsBVersion,
                state.NacP, state.Sil, state.Latitude, state.Longitude,
                message.TypeCode, message.AirspeedKnots, message.HeadingDegrees,
                message.IsTrueAirspeed, message.SelectedHeadingIsTrack, message.NicA,
                message.NicBaro, message.IsOnGround, message.IsOddCpr,
                message.CprLatitude, message.CprLongitude,
                Convert.ToHexString(frame.Bytes), frame.StreamId, frame.SamplePosition,
                frame.SignalQuality);
            ExportRecord persisted = (settings.SaveRawModeS ? record : record with { RawHex = string.Empty }) with
            {
                RecordType = settings.HistoryRecordMode
            };
            history.Add(persisted);
            if (history.Count > settings.MaximumHistory) history.RemoveRange(0, history.Count - settings.MaximumHistory);
            AppendHistory(persisted);

            AdsBMessageRow msgRow = new()
            {
                ReceivedAt = frame.ReceivedAt.ToLocalTime(),
                Icao = message.Icao,
                Callsign = state.Callsign,
                Kind = message.Kind,
                Summary = message.Summary,
                AltitudeFeet = state.AltitudeFeet,
                SpeedKnots = state.GroundSpeedKnots ?? state.AirspeedKnots
            };
            QueueMessageForView(msgRow);
        }

        string details = JsonSerializer.Serialize(new
        {
            message.Icao,
            message.TypeCode,
            message.Kind,
            message.Callsign,
            message.AltitudeFeet,
            message.IsGeometricAltitude,
            message.GroundSpeedKnots,
            message.TrackDegrees,
            message.AirspeedKnots,
            message.HeadingDegrees,
            message.IsTrueAirspeed,
            message.VerticalRateFeetPerMinute,
            message.SelectedAltitudeFeet,
            message.SelectedHeadingDegrees,
            message.SelectedHeadingIsTrack,
            message.EmergencyState,
            message.Squawk,
            message.AdsBVersion,
            message.NacP,
            message.Sil,
            message.NicA,
            message.NicBaro,
            message.IsOnGround,
            Latitude = position?.Latitude,
            Longitude = position?.Longitude,
            Raw = Convert.ToHexString(frame.Bytes)
        });
        host?.Notifications.PlayShortReceptionAlarm();
        ResultPublished?.Invoke(this, new(new PluginResultSummary(
            $"{message.Icao}-{frame.SamplePosition}", Descriptor.Id, frame.ReceivedAt, frame.StreamId,
            message.Kind, EmergencySeverity(message.EmergencyState), message.Icao, message.Summary,
            FrequencyHz, frame.SignalQuality, 1, details)));
    }
}
