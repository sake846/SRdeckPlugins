using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Ais.Dsp;
using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Ais.ViewModels;
using SRdeckPlugin.Ais.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais;

public sealed partial class AisPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<ExportRecord> persisted = HostContext is null
            ? []
            : PluginJsonLinesHistory.LoadAll<ExportRecord>(GetHistoryPath(HostContext));
        ExportRecord[] records;
        if (persisted.Count > 0)
        {
            records = persisted.Where(item =>
                (request.From is null || item.ReceivedAt >= request.From) &&
                (request.To is null || item.ReceivedAt <= request.To))
                    .Where(item => settings.ChannelFilter == "both" ||
                        (settings.ChannelFilter == "ais1" && item.Channel.Contains("1", StringComparison.OrdinalIgnoreCase)) ||
                        (settings.ChannelFilter == "ais2" && item.Channel.Contains("2", StringComparison.OrdinalIgnoreCase)))
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
                var csv = new StringBuilder("receivedAt,channel,frequencyHz,mmsi,messageType,repeatIndicator,kind,name,callSign," +
                    "imoNumber,latitude,longitude,speedKnots,courseDegrees,headingDegrees,rateOfTurn,navigationStatus," +
                    "shipType,destination,draughtMetres,dimensionToBowMetres,dimensionToSternMetres,dimensionToPortMetres," +
                    "dimensionToStarboardMetres,positionAccurate,utcSecond,aidType,summary,rawHex,streamId,samplePosition,signalQuality\r\n");
                foreach (ExportRecord item in records)
                {
                    csv.Append(Csv(item.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                        .Append(Csv(item.Channel)).Append(',').Append(item.FrequencyHz).Append(',')
                        .Append(item.Mmsi.ToString("000000000", CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.MessageType).Append(',').Append(item.RepeatIndicator).Append(',').Append(Csv(item.Kind)).Append(',')
                        .Append(Csv(item.Name)).Append(',').Append(Csv(item.CallSign)).Append(',')
                        .Append(Invariant(item.ImoNumber)).Append(',')
                        .Append(Invariant(item.Latitude)).Append(',').Append(Invariant(item.Longitude)).Append(',')
                        .Append(Invariant(item.SpeedKnots)).Append(',').Append(Invariant(item.CourseDegrees)).Append(',')
                        .Append(Invariant(item.HeadingDegrees)).Append(',').Append(Invariant(item.RateOfTurn)).Append(',')
                        .Append(Invariant(item.NavigationStatus)).Append(',').Append(Invariant(item.ShipType)).Append(',')
                        .Append(Csv(item.Destination)).Append(',').Append(Invariant(item.DraughtMetres)).Append(',')
                        .Append(Invariant(item.DimensionToBowMetres)).Append(',').Append(Invariant(item.DimensionToSternMetres)).Append(',')
                        .Append(Invariant(item.DimensionToPortMetres)).Append(',').Append(Invariant(item.DimensionToStarboardMetres)).Append(',')
                        .Append(item.PositionAccurate ? "true" : "false").Append(',').Append(Invariant(item.UtcSecond)).Append(',')
                        .Append(Csv(item.AidType)).Append(',').Append(Csv(item.Summary)).Append(',')
                        .Append(Csv(item.RawHex)).Append(',').Append(item.StreamId.ToString("D")).Append(',')
                        .Append(item.SamplePosition.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.SignalQuality.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                await File.WriteAllTextAsync(request.DestinationPath, csv.ToString(), new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            else return new(false, 0, $"Unknown AIS export format '{request.FormatId}'.");
            return new(true, records.Length, $"Exported {records.Length} AIS messages.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, 0, exception.Message);
        }
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        lock (processingGate)
        {
            channelA.Reset();
            channelB.Reset();
        }
        if (hostContext is not null) hostContext.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
    }

    private void SubmitMonitorAudio(ReadOnlySpan<float> audio, IqBlockMetadata metadata)
    {
        IPluginHostContext? hostContext = HostContext;
        if (audio.IsEmpty || hostContext is null || !settings.MonitorAudioEnabled) return;
        const float volume = 1f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(
                Math.Clamp(audio[index] * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        hostContext.Audio.TrySubmit(new PcmAudioFrame(
            hostContext.PluginId, metadata.StreamId, Interlocked.Increment(ref audioSequence),
            AisReceiver.MonitorAudioSampleRateHz, 1, PcmSampleFormat.Signed16LittleEndian,
            pcm, metadata.Discontinuity != IqDiscontinuity.None));
    }

    private void Publish(AisFrame frame, AisMessage message)
    {
        bool saveRecord = IsChannelSelectedForStorage(frame.Channel);
        string displayName;
        lock (gate)
        {
            if (!targets.TryGetValue(message.Mmsi, out AisTargetState? target))
            {
                target = new(message.Mmsi);
                targets.Add(message.Mmsi, target);
            }
            target.Apply(message, frame);
            displayName = target.VesselName;
            ExportRecord record = new(frame.ReceivedAt, frame.Channel, frame.FrequencyHz, message.Mmsi,
                message.MessageType, message.RepeatIndicator, message.Kind, message.VesselName, message.CallSign,
                message.ImoNumber,
                message.Latitude, message.Longitude, message.SpeedOverGroundKnots,
                message.CourseOverGroundDegrees, message.TrueHeadingDegrees,
                message.RateOfTurnDegreesPerMinute, message.NavigationStatus, message.ShipType, message.Destination,
                message.DraughtMetres, message.DimensionToBowMetres, message.DimensionToSternMetres,
                message.DimensionToPortMetres, message.DimensionToStarboardMetres,
                message.PositionAccurate, message.UtcSecond, message.AidType, message.Summary,
                settings.SaveRawFrames ? Convert.ToHexString(frame.Payload) : string.Empty,
                frame.StreamId, frame.SamplePosition, frame.SignalQuality);
            if (saveRecord)
            {
                history.Add(record);
                if (history.Count > settings.MaximumHistory)
                    history.RemoveRange(0, history.Count - settings.MaximumHistory);
                AppendHistory(record);
            }
        }
        var row = new AisMessageRow(frame.ReceivedAt.ToLocalTime(), frame.Channel, message.Mmsi,
            message.MessageType, message.Kind, displayName, message.Summary,
            message.Latitude, message.Longitude, message.SpeedOverGroundKnots,
            message.CourseOverGroundDegrees);
        QueueMessage(row);

        string details = JsonSerializer.Serialize(new
        {
            message.MessageType,
            message.Mmsi,
            frame.Channel,
            message.Latitude,
            message.Longitude,
            message.SpeedOverGroundKnots,
            message.CourseOverGroundDegrees
        });
        ResultPublished?.Invoke(this, new(new(
            $"ais-{frame.Channel.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-{frame.SamplePosition}",
            Descriptor.Id,
            frame.ReceivedAt,
            frame.StreamId,
            message.Kind,
            PluginResultSeverity.Information,
            $"AIS {message.MessageType} / {message.Mmsi:000000000}",
            message.Summary,
            frame.FrequencyHz,
            frame.SignalQuality,
            1,
            details)));
    }

    private void QueueMessage(AisMessageRow row)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        bool post;
        lock (pendingGate)
        {
            pendingMessages.Enqueue(row);
            post = !messageDrainPosted;
            messageDrainPosted = true;
        }
        if (post) context.Dispatcher.Post(DrainMessages);
    }

    private void DrainMessages()
    {
        while (true)
        {
            AisMessageRow[] batch;
            lock (pendingGate)
            {
                if (pendingMessages.Count == 0)
                {
                    messageDrainPosted = false;
                    return;
                }
                batch = pendingMessages.Take(100).ToArray();
                foreach (AisMessageRow _ in batch) pendingMessages.Dequeue();
            }
            foreach (AisMessageRow row in batch) viewModel.AddMessage(row);
        }
    }

    private void PublishViewSnapshot()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        AisTargetState[] snapshot;
        DateTimeOffset cutoff = context.TimeProvider.GetUtcNow().AddMinutes(-settings.RetentionMinutes);
        lock (gate)
        {
            foreach (uint stale in targets.Where(item => item.Value.LastSeen < cutoff).Select(item => item.Key).ToArray())
                targets.Remove(stale);
            PruneStoredHistoryUnsafe();
            if (targets.Count > settings.MaximumTargets)
            {
                foreach (uint excess in targets.Values.OrderByDescending(item => item.LastSeen)
                    .Skip(settings.MaximumTargets).Select(item => item.Mmsi).ToArray())
                    targets.Remove(excess);
            }
            snapshot = targets.Values.ToArray();
        }
        AisReceiver.DiagnosticsSnapshot diagnosticsA = channelA.GetDiagnostics();
        AisReceiver.DiagnosticsSnapshot diagnosticsB = channelB.GetDiagnostics();
        float? signalLevelDbm = context.ReceiverTelemetry?.SignalLevelDbm;
        context.Dispatcher.Post(() => viewModel.Apply(snapshot, diagnosticsA, diagnosticsB, signalLevelDbm));
    }
}
