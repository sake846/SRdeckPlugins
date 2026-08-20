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
    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddMinutes(-settings.RetentionMinutes);
        foreach (string key in aircraft.Where(item => item.Value.LastSeen < cutoff).Select(item => item.Key).ToArray())
            aircraft.Remove(key);
        while (aircraft.Count > settings.MaximumAircraft)
            aircraft.Remove(aircraft.MinBy(item => item.Value.LastSeen).Key);
    }

    private void PruneStoredHistory()
    {
        lock (gate)
        {
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
        }
    }

    private void LoadHistory()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            ExportRecord[] loaded = PluginJsonLinesHistory.Load<ExportRecord>(path, settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (gate)
            {
                history.AddRange(loaded);
                foreach (IGrouping<string, ExportRecord> group in loaded.GroupBy(item => item.Icao))
                {
                    ExportRecord item = group.Last();
                    aircraft[item.Icao] = new AircraftState
                    {
                        Icao = item.Icao,
                        Callsign = item.Callsign,
                        AltitudeFeet = item.GeometricAltitudeFeet ?? item.BarometricAltitudeFeet,
                        BarometricAltitudeFeet = item.BarometricAltitudeFeet,
                        GeometricAltitudeFeet = item.GeometricAltitudeFeet,
                        GroundSpeedKnots = item.SpeedKnots,
                        TrackDegrees = item.TrackDegrees,
                        VerticalRateFeetPerMinute = item.VerticalRate,
                        SelectedAltitudeFeet = item.SelectedAltitudeFeet,
                        SelectedHeadingDegrees = item.SelectedHeadingDegrees,
                        EmergencyState = item.EmergencyState,
                        Squawk = item.Squawk,
                        AdsBVersion = item.AdsBVersion,
                        NacP = item.NacP,
                        Sil = item.Sil,
                        Latitude = item.Latitude,
                        Longitude = item.Longitude,
                        LastSeen = item.ReceivedAt,
                        MessageCount = group.LongCount()
                    };
                }
                Prune(context.TimeProvider.GetUtcNow());
            }

            foreach (ExportRecord item in loaded.TakeLast(1000))
                QueueMessageForView(new AdsBMessageRow
                {
                    ReceivedAt = item.ReceivedAt.ToLocalTime(),
                    Icao = item.Icao,
                    Callsign = item.Callsign,
                    Kind = item.Kind,
                    Summary = string.IsNullOrWhiteSpace(item.Callsign)
                        ? item.Kind : $"{item.Kind} / {item.Callsign}",
                    AltitudeFeet = item.GeometricAltitudeFeet ?? item.BarometricAltitudeFeet,
                    SpeedKnots = item.SpeedKnots
                });
            PublishViewSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "adsb.history.load-failed",
                "ADS-B decoded history could not be loaded.", exception);
        }
    }

    private void AppendHistory(ExportRecord record)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        if (historyWriter?.TryEnqueue(record) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "adsb.history.queue-full",
            "ADS-B decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<ExportRecord> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<ExportRecord>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(
                settings.MaximumHistory),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "adsb.history.save-failed",
            "ADS-B decoded history could not be saved.", exception);
        return writer;
    }

    private void DeleteHistoryFile()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            historyWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            historyWriter = CreateHistoryWriter(context);
            PluginJsonLinesHistory.Delete(GetHistoryPath(context));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "adsb.history.delete-failed",
                "ADS-B decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private void PublishViewSnapshot()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        AircraftState[] snapshot;
        lock (gate) snapshot = aircraft.Values.Select(Clone).ToArray();
        long valid = receiver.ValidFrameCount;
        long rejected = receiver.RejectedFrameCount;
        long sicRecovered = receiver.SicRecoveredFrameCount;
        long timingAdjusted = receiver.TimingAdjustedFrameCount;
        ModeSReceiver.DiagnosticsSnapshot diagnostics = receiver.GetDiagnostics();
        float? noiseFloorDbm = context.ReceiverTelemetry?.NoiseFloorDbm;
        context.Dispatcher.Post(() => viewModel.Apply(snapshot, valid, rejected, sicRecovered, timingAdjusted,
            diagnostics, noiseFloorDbm));
    }

    private void QueueMessageForView(AdsBMessageRow message)
    {
        IPluginHostContext? context = host;
        if (context is null) return;

        bool postDrain = false;
        lock (pendingMessageGate)
        {
            // The view retains 1,000 rows.  Keep the same bound before the UI dispatcher
            // sees them so a stalled UI cannot accumulate an unbounded work queue.
            while (pendingMessages.Count >= 1_000) pendingMessages.Dequeue();
            pendingMessages.Enqueue(message);
            if (!messageDrainPosted)
            {
                messageDrainPosted = true;
                postDrain = true;
            }
        }

        if (postDrain) context.Dispatcher.Post(DrainPendingMessages);
    }

    private void DrainPendingMessages()
    {
        const int batchSize = 32;
        AdsBMessageRow[] batch;
        bool postNext;
        lock (pendingMessageGate)
        {
            int count = Math.Min(batchSize, pendingMessages.Count);
            batch = new AdsBMessageRow[count];
            for (int index = 0; index < count; index++) batch[index] = pendingMessages.Dequeue();
            postNext = pendingMessages.Count > 0;
            messageDrainPosted = postNext;
        }

        foreach (AdsBMessageRow message in batch) viewModel.AddMessage(message);
        if (postNext) host?.Dispatcher.Post(DrainPendingMessages);
    }

    private void SetStatus(string value)
    {
        IPluginHostContext? context = host;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            if (result.SampleRateHz < ModeSReceiver.MinimumInputSampleRateHz)
            {
                SetStatus(GetMinimumSampleRateMessage(result.SampleRateHz));
                return;
            }
            if ((result.PassbandLowerFrequencyHz > FrequencyHz ||
                 result.PassbandUpperFrequencyHz < FrequencyHz) && host is not null)
            {
                _ = host.Tuning.RequestAsync(new PluginTuningRequest(
                    "adsb-1090",
                    "ADS-B 1090 MHz",
                    [new TuningTarget(FrequencyHz, 1_900_000)],
                    FrequencyHz,
                    2_000_000,
                    null,
                    true,
                    false,
                    PluginGainPreference.Automatic));
            }
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                $"{result.CenterFrequencyHz / 1_000_000.0:F3} MHz / {result.SampleRateHz / 1_000_000.0:F2} MS/s");
        }
    }

    private static string GetMinimumSampleRateMessage(int sampleRateHz) =>
        sampleRateHz > 0
            ? $"ADS-B は 2.0 MS/s 以上が必要です（現在 {sampleRateHz / 1_000_000.0:F1} MS/s）。受信を停止して RATE を 2 MS/s 以上に変更してください。"
            : "ADS-B は 2.0 MS/s 以上が必要です。受信を停止して RATE を 2 MS/s 以上に変更してください。";

    private static AircraftState Clone(AircraftState value) => new()
    {
        Icao = value.Icao,
        Callsign = value.Callsign,
        AltitudeFeet = value.AltitudeFeet,
        BarometricAltitudeFeet = value.BarometricAltitudeFeet,
        GeometricAltitudeFeet = value.GeometricAltitudeFeet,
        GroundSpeedKnots = value.GroundSpeedKnots,
        TrackDegrees = value.TrackDegrees,
        AirspeedKnots = value.AirspeedKnots,
        HeadingDegrees = value.HeadingDegrees,
        SelectedAltitudeFeet = value.SelectedAltitudeFeet,
        SelectedHeadingDegrees = value.SelectedHeadingDegrees,
        SelectedHeadingIsTrack = value.SelectedHeadingIsTrack,
        EmergencyState = value.EmergencyState,
        Squawk = value.Squawk,
        AdsBVersion = value.AdsBVersion,
        NacP = value.NacP,
        Sil = value.Sil,
        NicA = value.NicA,
        NicBaro = value.NicBaro,
        IsOnGround = value.IsOnGround,
        VerticalRateFeetPerMinute = value.VerticalRateFeetPerMinute,
        Latitude = value.Latitude,
        Longitude = value.Longitude,
        LastSeen = value.LastSeen,
        MessageCount = value.MessageCount
    };
    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    private static string Invariant(object? value) => value is IFormattable item ? item.ToString(null, CultureInfo.InvariantCulture) : string.Empty;
    private static PluginResultSeverity EmergencySeverity(string? emergency) => emergency switch
    {
        "general emergency" or "unlawful interference" or "downed aircraft" => PluginResultSeverity.Critical,
        "medical" or "minimum fuel" or "no communications" => PluginResultSeverity.Warning,
        _ => PluginResultSeverity.Information
    };
    private void StartIqCapture()
    {
        if (State != PluginLifecycleState.Streaming || host is null)
        {
            viewModel.CaptureStatus = "IQ録音: 受信中に開始してください";
            return;
        }
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
        {
            viewModel.CaptureStatus = "IQ録音: 保存処理中です";
            return;
        }
        IPluginHostContext context = host;
        IqBlockMetadata? metadata = lastCaptureMetadata;
        ModeSReceiver.DiagnosticsSnapshot diagnostics = receiver.GetDiagnostics();
        int activeAircraftCount;
        lock (gate) activeAircraftCount = aircraft.Count;
        viewModel.CaptureStatus = "IQ録音: 直前3秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context, metadata, diagnostics, activeAircraftCount));
    }

    private void SavePretriggerCapture(IPluginHostContext context, IqBlockMetadata? metadata,
        ModeSReceiver.DiagnosticsSnapshot diagnostics, int activeAircraftCount)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"adsb-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-1090MHz");
            string path = $"{basePath}.wav";
            using (var capture = new AdsBIqCapture(path, snapshot.SampleRateHz, TimeSpan.FromSeconds(3)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }
            var document = new
            {
                Format = "SRdeck ADS-B analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
                FrequencyHz = FrequencyHz,
                ActiveAircraftCount = activeAircraftCount,
                RawIqFile = Path.GetFileName(path),
                SampleRateHz = snapshot.SampleRateHz,
                DurationSeconds = snapshot.DurationSeconds,
                InputMetadata = metadata,
                ReceiverDiagnostics = diagnostics
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                }));
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存済み: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "adsb.iq-capture.save-failed",
                "Could not save ADS-B rolling IQ capture.", exception);
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }

    public sealed record ExportRecord(DateTimeOffset ReceivedAt, string Icao, string Kind, string Callsign,
        int? BarometricAltitudeFeet, int? GeometricAltitudeFeet, double? SpeedKnots,
        double? TrackDegrees, int? VerticalRate, int? SelectedAltitudeFeet,
        double? SelectedHeadingDegrees, string EmergencyState, string Squawk,
        int? AdsBVersion, int? NacP, int? Sil, double? Latitude, double? Longitude,
        int TypeCode = 0, double? AirspeedKnots = null, double? HeadingDegrees = null,
        bool? IsTrueAirspeed = null, bool? SelectedHeadingIsTrack = null,
        bool? NicA = null, bool? NicBaro = null, bool? IsOnGround = null,
        bool? IsOddCpr = null, int? CprLatitude = null, int? CprLongitude = null,
        string RawHex = "", Guid StreamId = default, long SamplePosition = 0,
        double SignalQuality = 0, string RecordType = "both");
}
