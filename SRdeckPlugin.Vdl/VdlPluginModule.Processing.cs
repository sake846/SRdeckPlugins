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

        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            long targetFrequencyHz = Channels.First(channel => channel.Id == selectedProfileId).FrequencyHz;
            IqBlockMetadata metadata = block.Metadata;
            lastCaptureMetadata = metadata;
            long processingStarted = Stopwatch.GetTimestamp();
            float[]? monitorBuffer = null;
            Complex32[]? channelBuffer = null;
            VdlDecodedFrame[] decodedFrames;
            VdlPipelineDiagnosticsSnapshot pipeline;
            try
            {
                int capacity = checked((int)Math.Ceiling(block.Samples.Length *
                    (double)VdlMode2Receiver.WorkingSampleRate / metadata.SampleRateHz) + 4);
                if (settings.MonitorAudioEnabled)
                    monitorBuffer = ArrayPool<float>.Shared.Rent(capacity / 5 + 4);
                channelBuffer = ArrayPool<Complex32>.Shared.Rent(capacity);
                IReadOnlyList<VdlFrame> receivedFrames = receiver.Process(block.Samples.Span, metadata, targetFrequencyHz,
                    monitorBuffer, out int audioSampleCount, channelBuffer, out int channelSampleCount);
                TimeSpan receiverElapsed = Stopwatch.GetElapsedTime(processingStarted);
                long audioStarted = Stopwatch.GetTimestamp();
                bool audioSubmitted = true;
                bool discontinuous = metadata.Discontinuity != IqDiscontinuity.None;
                if (settings.MonitorAudioEnabled && monitorBuffer is not null)
                {
                    bool resetAudio = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (resetAudio) host?.Audio.Reset();
                    audioSubmitted = SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, resetAudio);
                }
                TimeSpan audioElapsed = Stopwatch.GetElapsedTime(audioStarted);
                long pretriggerStarted = Stopwatch.GetTimestamp();
                if (channelBuffer is not null)
                    pretriggerBuffer.Write(block.Samples.Span, metadata.SampleRateHz,
                        channelBuffer.AsSpan(0, channelSampleCount), VdlMode2Receiver.WorkingSampleRate);
                TimeSpan pretriggerElapsed = Stopwatch.GetElapsedTime(pretriggerStarted);
                long protocolStarted = Stopwatch.GetTimestamp();
                decodedFrames = receivedFrames.Select(upperLayerDecoder.Decode).ToArray();
                TimeSpan protocolElapsed = Stopwatch.GetElapsedTime(protocolStarted);
                pipeline = pipelineDiagnostics.Record(metadata, methodStarted, Stopwatch.GetElapsedTime(methodStarted),
                    Stopwatch.GetElapsedTime(processingStarted), receiverElapsed, audioElapsed,
                    pretriggerElapsed, protocolElapsed, audioSubmitted, discontinuous);
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
                if (channelBuffer is not null) ArrayPool<Complex32>.Shared.Return(channelBuffer);
            }

            PublishDecodedFrames(decodedFrames, pipeline);
        }
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

        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            ChannelIqBlockMetadata channelMetadata = block.Metadata;
            IqBlockMetadata metadata = channelMetadata.Source;
            lastCaptureMetadata = metadata;
            long processingStarted = Stopwatch.GetTimestamp();
            float[]? monitorBuffer = null;
            VdlDecodedFrame[] decodedFrames;
            VdlPipelineDiagnosticsSnapshot pipeline;
            try
            {
                if (settings.MonitorAudioEnabled)
                    monitorBuffer = ArrayPool<float>.Shared.Rent(block.Samples.Length / 5 + 4);
                IReadOnlyList<VdlFrame> receivedFrames = receiver.ProcessChannel(
                    block.Samples.Span, channelMetadata, monitorBuffer, out int audioSampleCount);
                TimeSpan receiverElapsed = Stopwatch.GetElapsedTime(processingStarted);
                long audioStarted = Stopwatch.GetTimestamp();
                bool audioSubmitted = true;
                bool discontinuous = metadata.Discontinuity != IqDiscontinuity.None;
                if (settings.MonitorAudioEnabled && monitorBuffer is not null)
                {
                    bool resetAudio = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (resetAudio) host?.Audio.Reset();
                    audioSubmitted = SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, resetAudio);
                }
                TimeSpan audioElapsed = Stopwatch.GetElapsedTime(audioStarted);
                long pretriggerStarted = Stopwatch.GetTimestamp();
                pretriggerBuffer.Write(
                    block.Samples.Span, channelMetadata.Configuration.OutputSampleRateHz,
                    block.Samples.Span, channelMetadata.Configuration.OutputSampleRateHz);
                TimeSpan pretriggerElapsed = Stopwatch.GetElapsedTime(pretriggerStarted);
                long protocolStarted = Stopwatch.GetTimestamp();
                decodedFrames = receivedFrames.Select(upperLayerDecoder.Decode).ToArray();
                TimeSpan protocolElapsed = Stopwatch.GetElapsedTime(protocolStarted);
                pipeline = pipelineDiagnostics.Record(metadata, methodStarted,
                    Stopwatch.GetElapsedTime(methodStarted), Stopwatch.GetElapsedTime(processingStarted),
                    receiverElapsed, audioElapsed, pretriggerElapsed, protocolElapsed,
                    audioSubmitted, discontinuous);
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
            }
            PublishDecodedFrames(decodedFrames, pipeline);
        }
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
                    upperLayerDecoder.Reset();
                }
            },
            cancellationToken);
    }

    private void PublishDecodedFrames(IReadOnlyList<VdlDecodedFrame> decodedFrames,
        VdlPipelineDiagnosticsSnapshot pipeline)
    {
        VdlMode2Receiver.DiagnosticsSnapshot diag = receiver.GetDiagnostics();
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
        long validCount = receiver.ValidFrameCount;
        long rejectedCount = receiver.RejectedFrameCount;
        long syncCount = receiver.SynchronizationCount;
        long offsetHz = receiver.FrequencyOffsetHz;
        float? signalLevelDbm = host?.ReceiverTelemetry?.SignalLevelDbm;
        float? noiseFloorDbm = host?.ReceiverTelemetry?.NoiseFloorDbm;
        host?.Dispatcher.Post(() =>
        {
            foreach (VdlDecodedFrame decoded in enrichedFrames) viewModel.AddFrame(decoded);
            viewModel.UpdateDiagnostics(diag, validCount, rejectedCount, syncCount, offsetHz, pipeline, signalLevelDbm, noiseFloorDbm);
        });
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (processingGate)
        {
            receiver.Reset();
            upperLayerDecoder.Reset();
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
