using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Acars.Dsp;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Acars.ViewModels;
using SRdeckPlugin.Acars.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars;

public sealed partial class AcarsPluginModule
{
    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        IqBlockMetadata metadata = block.Metadata;
        lastCaptureMetadata = metadata;

        int capacity = checked((int)Math.Ceiling(
            block.Samples.Length * (double)AcarsReceiver.DemodulationSampleRateHz /
            metadata.SampleRateHz) + 2);
        float[][]? channelAudio = null;
        try
        {
            lock (processingGate)
            {
                if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
                bool isDiscontinuous = continuity.Observe(metadata).RequiresReset;
                if (isDiscontinuous)
                {
                    foreach (AcarsReceiver receiver in ReceiverSnapshot())
                        receiver.Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
                    host?.Audio.Reset();
                }
                pretriggerBuffer.Write(block.Samples.Span, metadata.SampleRateHz);
                (Channel primaryChannel, (Channel Channel, AcarsReceiver Receiver)[] targets) =
                    ReceiverProcessingPlan();
                if (targets.Length == 0) return ValueTask.CompletedTask;
                bool requestedAudioDiscontinuity =
                    Interlocked.Exchange(ref audioDiscontinuityPending, 0) != 0;
                if (requestedAudioDiscontinuity) host?.Audio.Reset();
                int primaryIndex = Array.FindIndex(targets,
                    target => target.Channel.Id == primaryChannel.Id);
                if (primaryIndex < 0) primaryIndex = 0;
                AcarsReceiver primaryReceiver = targets[primaryIndex].Receiver;
                var frameResults = new IReadOnlyList<AcarsFrame>[targets.Length];
                var audioSampleCounts = new int[targets.Length];
                channelAudio = new float[targets.Length][];
                for (int index = 0; index < channelAudio.Length; index++)
                    channelAudio[index] = ArrayPool<float>.Shared.Rent(capacity);
                if (targets.Length == 1)
                {
                    frameResults[0] = primaryReceiver.Process(block.Samples.Span, metadata,
                        primaryChannel.FrequencyHz, channelAudio[0].AsSpan(0, capacity),
                        out audioSampleCounts[0]);
                }
                else
                {
                    Parallel.For(0, targets.Length, new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = MaximumChannelParallelism(targets.Length)
                    }, index =>
                    {
                        (Channel channel, AcarsReceiver receiver) = targets[index];
                        frameResults[index] = receiver.Process(block.Samples.Span, metadata,
                            channel.FrequencyHz, channelAudio[index].AsSpan(0, capacity),
                            out audioSampleCounts[index]);
                    });
                }
                int audioSampleCount = audioSampleCounts.Length > 0 ? audioSampleCounts.Min() : 0;
                int openAudioChannelCount = MixSquelchedChannelAudio(
                    targets, channelAudio, audioSampleCount);
                if (channelAudio.Length > 0)
                {
                    SubmitAudio(channelAudio[0].AsSpan(0, audioSampleCount), metadata,
                        isDiscontinuous || requestedAudioDiscontinuity);
                }
                UpdateDiagnostics(metadata, primaryReceiver, targets.Length,
                    openAudioChannelCount);
                foreach (AcarsFrame frame in frameResults.SelectMany(result => result))
                    if (AcarsMessageParser.TryParse(frame, out AcarsMessage? message) && message is not null)
                        foreach (ReassembledMessage completed in messageReassembler.Process(frame, message))
                            Publish(completed.Frame, completed.Message);
            }
        }
        finally
        {
            if (channelAudio is not null)
                foreach (float[] audio in channelAudio)
                    if (audio is not null) ArrayPool<float>.Shared.Return(audio);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming || blocks.Count == 0)
            return ValueTask.CompletedTask;
        IqBlockMetadata metadata = blocks[0].Metadata.Source;
        lastCaptureMetadata = metadata;
        float[][]? channelAudio = null;
        try
        {
            lock (processingGate)
            {
                if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
                bool isDiscontinuous = continuity.Observe(metadata).RequiresReset;
                if (isDiscontinuous) host?.Audio.Reset();
                (Channel primaryChannel, (Channel Channel, AcarsReceiver Receiver)[] targets) =
                    ReceiverProcessingPlan();
                if (targets.Length == 0) return ValueTask.CompletedTask;
                bool requestedAudioDiscontinuity =
                    Interlocked.Exchange(ref audioDiscontinuityPending, 0) != 0;
                if (requestedAudioDiscontinuity) host?.Audio.Reset();
                var activeJobs = new List<(IChannelIqBlockLease Block, Channel Channel, AcarsReceiver Receiver)>();
                foreach (IChannelIqBlockLease block in blocks)
                {
                    string requestId = block.Metadata.Configuration.RequestId;
                    string channelId = requestId.StartsWith("acars-", StringComparison.Ordinal)
                        ? requestId.Substring(6)
                        : requestId;
                    int targetIndex = Array.FindIndex(targets, t => string.Equals(t.Channel.Id, channelId, StringComparison.Ordinal));
                    if (targetIndex >= 0)
                        activeJobs.Add((block, targets[targetIndex].Channel, targets[targetIndex].Receiver));
                }
                if (activeJobs.Count == 0) return ValueTask.CompletedTask;
                int primaryIndex = activeJobs.FindIndex(job => job.Channel.Id == primaryChannel.Id);
                if (primaryIndex < 0) primaryIndex = 0;
                var frameResults = new IReadOnlyList<AcarsFrame>[activeJobs.Count];
                var audioSampleCounts = new int[activeJobs.Count];
                channelAudio = new float[activeJobs.Count][];
                for (int index = 0; index < activeJobs.Count; index++)
                {
                    channelAudio[index] = ArrayPool<float>.Shared.Rent(activeJobs[index].Block.Samples.Length + 2);
                }
                Parallel.For(0, activeJobs.Count, new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaximumChannelParallelism(activeJobs.Count)
                }, index =>
                {
                    var job = activeJobs[index];
                    frameResults[index] = job.Receiver.ProcessChannel(
                        job.Block.Samples.Span, job.Block.Metadata,
                        channelAudio[index].AsSpan(0, job.Block.Samples.Length + 2),
                        out audioSampleCounts[index]);
                });
                int audioSampleCount = audioSampleCounts.Min();
                var activeTargets = activeJobs.Select(job => (job.Channel, job.Receiver)).ToArray();
                int openAudioChannelCount = MixSquelchedChannelAudio(
                    activeTargets, channelAudio, audioSampleCount);
                SubmitAudio(channelAudio[0].AsSpan(0, audioSampleCount), metadata,
                    isDiscontinuous || requestedAudioDiscontinuity);
                pretriggerBuffer.Write(activeJobs[primaryIndex].Block.Samples.Span,
                    activeJobs[primaryIndex].Block.Metadata.Configuration.OutputSampleRateHz);
                AcarsReceiver primaryReceiver = activeJobs[primaryIndex].Receiver;
                UpdateDiagnostics(metadata, primaryReceiver, activeJobs.Count, openAudioChannelCount);
                foreach (AcarsFrame frame in frameResults.SelectMany(result => result))
                    if (AcarsMessageParser.TryParse(frame, out AcarsMessage? message) && message is not null)
                        foreach (ReassembledMessage completed in messageReassembler.Process(frame, message))
                            Publish(completed.Frame, completed.Message);
            }
        }
        finally
        {
            if (channelAudio is not null)
                foreach (float[] audio in channelAudio)
                    if (audio is not null) ArrayPool<float>.Shared.Return(audio);
        }
        return ValueTask.CompletedTask;
    }
}
