using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Vdl.Dsp;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;
using SRdeckPlugin.Vdl.ViewModels;
using SRdeckPlugin.Vdl.Views;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl;

public sealed partial class VdlPluginModule
{
    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        long methodStarted = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;

        IqBlockMetadata metadata = block.Metadata;
        IReadOnlyList<VdlFrame> receivedFrames;
        TimeSpan receiverElapsed;
        TimeSpan audioElapsed;
        TimeSpan pretriggerElapsed;
        TimeSpan lockedProcessingElapsed;
        bool audioSubmitted;
        bool discontinuous;
        ReceiverPublishSnapshot receiverSnapshot;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            long targetFrequencyHz = Channels.First(channel => channel.Id == selectedProfileId).FrequencyHz;
            lastCaptureMetadata = metadata;
            long processingStarted = Stopwatch.GetTimestamp();
            float[]? monitorBuffer = null;
            Complex32[]? channelBuffer = null;
            try
            {
                int capacity = checked((int)Math.Ceiling(block.Samples.Length *
                    (double)VdlMode2Receiver.WorkingSampleRate / metadata.SampleRateHz) + 4);
                if (settings.MonitorAudioEnabled)
                    monitorBuffer = ArrayPool<float>.Shared.Rent(capacity / 5 + 4);
                channelBuffer = ArrayPool<Complex32>.Shared.Rent(capacity);
                receivedFrames = receiver.Process(block.Samples.Span, metadata, targetFrequencyHz,
                    monitorBuffer, out int audioSampleCount, channelBuffer, out int channelSampleCount);
                receiverElapsed = Stopwatch.GetElapsedTime(processingStarted);
                long audioStarted = Stopwatch.GetTimestamp();
                audioSubmitted = true;
                discontinuous = metadata.Discontinuity != IqDiscontinuity.None;
                if (settings.MonitorAudioEnabled && monitorBuffer is not null)
                {
                    bool resetAudio = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (resetAudio) host?.Audio.Reset();
                    audioSubmitted = SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, resetAudio);
                }
                audioElapsed = Stopwatch.GetElapsedTime(audioStarted);
                long pretriggerStarted = Stopwatch.GetTimestamp();
                if (channelBuffer is not null)
                    pretriggerBuffer.Write(block.Samples.Span, metadata.SampleRateHz,
                        channelBuffer.AsSpan(0, channelSampleCount), VdlMode2Receiver.WorkingSampleRate);
                pretriggerElapsed = Stopwatch.GetElapsedTime(pretriggerStarted);
                receiverSnapshot = CaptureReceiverPublishSnapshot();
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
                if (channelBuffer is not null) ArrayPool<Complex32>.Shared.Return(channelBuffer);
            }
            lockedProcessingElapsed = Stopwatch.GetElapsedTime(processingStarted);
        }
        long protocolStarted = Stopwatch.GetTimestamp();
        VdlDecodedFrame[] decodedFrames = DecodeUpperLayers(receivedFrames);
        TimeSpan protocolElapsed = Stopwatch.GetElapsedTime(protocolStarted);
        VdlPipelineDiagnosticsSnapshot pipeline = pipelineDiagnostics.Record(
            metadata, methodStarted, Stopwatch.GetElapsedTime(methodStarted),
            lockedProcessingElapsed + protocolElapsed, receiverElapsed, audioElapsed,
            pretriggerElapsed, protocolElapsed, audioSubmitted, discontinuous);
        PublishDecodedFrames(decodedFrames, pipeline, receiverSnapshot);
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        long methodStarted = Stopwatch.GetTimestamp();
        if (blocks.Count != 1)
            throw new ArgumentException("VDL2 requires exactly one standard channel block.", nameof(blocks));
        IChannelIqBlockLease block = blocks[0];
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;

        ChannelIqBlockMetadata channelMetadata = block.Metadata;
        IqBlockMetadata metadata = channelMetadata.Source;
        IReadOnlyList<VdlFrame> receivedFrames;
        TimeSpan receiverElapsed;
        TimeSpan audioElapsed;
        TimeSpan pretriggerElapsed;
        TimeSpan lockedProcessingElapsed;
        bool audioSubmitted;
        bool discontinuous;
        ReceiverPublishSnapshot receiverSnapshot;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            lastCaptureMetadata = metadata;
            long processingStarted = Stopwatch.GetTimestamp();
            float[]? monitorBuffer = null;
            try
            {
                if (settings.MonitorAudioEnabled)
                    monitorBuffer = ArrayPool<float>.Shared.Rent(block.Samples.Length / 5 + 4);
                receivedFrames = receiver.ProcessChannel(
                    block.Samples.Span, channelMetadata, monitorBuffer, out int audioSampleCount);
                receiverElapsed = Stopwatch.GetElapsedTime(processingStarted);
                long audioStarted = Stopwatch.GetTimestamp();
                audioSubmitted = true;
                discontinuous = metadata.Discontinuity != IqDiscontinuity.None;
                if (settings.MonitorAudioEnabled && monitorBuffer is not null)
                {
                    bool resetAudio = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (resetAudio) host?.Audio.Reset();
                    audioSubmitted = SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, resetAudio);
                }
                audioElapsed = Stopwatch.GetElapsedTime(audioStarted);
                long pretriggerStarted = Stopwatch.GetTimestamp();
                pretriggerBuffer.Write(
                    block.Samples.Span, channelMetadata.Configuration.OutputSampleRateHz,
                    block.Samples.Span, channelMetadata.Configuration.OutputSampleRateHz);
                pretriggerElapsed = Stopwatch.GetElapsedTime(pretriggerStarted);
                receiverSnapshot = CaptureReceiverPublishSnapshot();
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
            }
            lockedProcessingElapsed = Stopwatch.GetElapsedTime(processingStarted);
        }
        long protocolStarted = Stopwatch.GetTimestamp();
        VdlDecodedFrame[] decodedFrames = DecodeUpperLayers(receivedFrames);
        TimeSpan protocolElapsed = Stopwatch.GetElapsedTime(protocolStarted);
        VdlPipelineDiagnosticsSnapshot pipeline = pipelineDiagnostics.Record(
            metadata, methodStarted, Stopwatch.GetElapsedTime(methodStarted),
            lockedProcessingElapsed + protocolElapsed, receiverElapsed, audioElapsed,
            pretriggerElapsed, protocolElapsed, audioSubmitted, discontinuous);
        PublishDecodedFrames(decodedFrames, pipeline, receiverSnapshot);
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        Channel channel = Channels.First(item => item.Id == selectedProfileId);
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            $"vdl-{channel.Id}",
            channel.FrequencyHz,
            VdlMode2Receiver.WorkingSampleRate,
            16_800,
            (samples, metadata) =>
            {
                lock (processingGate)
                    receiver.ProcessChannel(samples, metadata, null, out _);
            },
            () =>
            {
                lock (processingGate)
                {
                    receiver.Reset();
                    lock (upperLayerGate) upperLayerDecoder.Reset();
                }
            },
            cancellationToken);
    }

    private VdlDecodedFrame[] DecodeUpperLayers(IReadOnlyList<VdlFrame> receivedFrames)
    {
        if (receivedFrames.Count == 0) return [];
        var decodedFrames = new VdlDecodedFrame[receivedFrames.Count];
        lock (upperLayerGate)
            for (int index = 0; index < receivedFrames.Count; index++)
                decodedFrames[index] = upperLayerDecoder.Decode(receivedFrames[index]);
        return decodedFrames;
    }

    private ReceiverPublishSnapshot CaptureReceiverPublishSnapshot() => new(
        receiver.GetDiagnostics(),
        receiver.ValidFrameCount,
        receiver.RejectedFrameCount,
        receiver.SynchronizationCount,
        receiver.FrequencyOffsetHz);

    private void PublishDecodedFrames(IReadOnlyList<VdlDecodedFrame> decodedFrames,
        VdlPipelineDiagnosticsSnapshot pipeline,
        ReceiverPublishSnapshot receiverSnapshot)
    {
        VdlMode2Receiver.DiagnosticsSnapshot diag = receiverSnapshot.Diagnostics;
        var enrichedFrames = new List<VdlDecodedFrame>(decodedFrames.Count);
        foreach (VdlDecodedFrame decoded in decodedFrames)
        {
            VdlDecodedFrame enriched = decoded with
            {
                Raw = decoded.Raw with
                {
                    SignalQuality = diag.PreambleSnrDb,
                    PreambleSnrDb = diag.PreambleSnrDb,
                    PreambleCoherence = diag.PreambleCoherence
                }
            };
            enrichedFrames.Add(enriched);
            Publish(enriched);
        }
        float? signalLevelDbm = host?.ReceiverTelemetry?.SignalLevelDbm;
        float? noiseFloorDbm = host?.ReceiverTelemetry?.NoiseFloorDbm;
        host?.Dispatcher.Post(() =>
        {
            foreach (VdlDecodedFrame decoded in enrichedFrames) viewModel.AddFrame(decoded);
            viewModel.UpdateDiagnostics(diag, receiverSnapshot.ValidFrameCount,
                receiverSnapshot.RejectedFrameCount, receiverSnapshot.SynchronizationCount,
                receiverSnapshot.FrequencyOffsetHz, pipeline, signalLevelDbm, noiseFloorDbm);
        });
    }

    private readonly record struct ReceiverPublishSnapshot(
        VdlMode2Receiver.DiagnosticsSnapshot Diagnostics,
        long ValidFrameCount,
        long RejectedFrameCount,
        long SynchronizationCount,
        long FrequencyOffsetHz);

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (processingGate)
        {
            receiver.Reset();
            lock (upperLayerGate) upperLayerDecoder.Reset();
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
}
