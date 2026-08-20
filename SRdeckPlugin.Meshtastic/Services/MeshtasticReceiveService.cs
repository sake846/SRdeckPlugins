using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.DSP;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Contracts;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Services;

/// <summary>
/// Receive-only Meshtastic sub-GHz front end. Raw IQ is copied from the
/// shared ring promptly, then channelized on a dedicated worker so audio and
/// display processing are not blocked by LoRa acquisition.
/// </summary>
public sealed partial class MeshtasticReceiveService : IMeshtasticReceiveService
{
    private const int QueueCapacity = 2048;
    private const int RingSafetyBlocks = 2;
    private const int RecentPacketCapacity = 4096;

    private readonly ConcurrentQueue<IqBlock> _blockQueue = new();
    private readonly BlockingCollection<IqBlock> _blocks;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly List<ChannelReceiver> _channelReceivers = new();
    private readonly Dictionary<MeshtasticPacketKey, int> _recentPackets = new();
    private readonly Queue<MeshtasticPacketKey> _recentPacketOrder = new();
    private readonly object _recentPacketsGate = new();
    private readonly object _drainGate = new();
    private readonly object _streamGate = new();
    private readonly SemaphoreSlim _streamLifecycleGate = new(1, 1);
    private ChannelConfiguration[] _channelConfigurations = [new(
        MeshtasticRegion.JP,
        MeshtasticJpLongFastProfile.DefaultChannel,
        MeshtasticJpLongFastProfile.FrequencyHz,
        MeshtasticJpLongFastProfile.GetProfile(MeshtasticModemPreset.LongFast),
        "LongFast",
        [.. MeshtasticChannelDecryptor.DefaultLongFastKey],
        MeshtasticChannelDecryptor.DefaultLongFastChannelHash,
        0)];
    private int _configurationVersion;
    private int _lastProcessedConfigurationVersion = -1;
    private long _submittedBlocks;
    private long _processedBlocks;
    private long _droppedBlocks;
    private long _timedBlockCount;
    private long _totalQueueDelayUs;
    private long _maximumQueueDelayUs;
    private long _currentQueueDelayUs;
    private long _totalProcessingTimeUs;
    private long _maximumProcessingTimeUs;
    private long _currentProcessingTimeUs;
    private long _totalChannelizationTicks;
    private long _totalDetectionTicks;
    private long _totalInputBlockTimeUs;
    private long _currentInputBlockTimeUs;
    private long _currentProcessingLoadBasisPoints;
    private int _lastInputSampleRateHz;
    private long _maximumProcessingLoadBasisPoints;
    private int _maximumQueueDepth;
    private long _detectedPreambles;
    private long _synchronizedFrames;
    private long _decodedHeaders;
    private long _decodedPayloads;
    private long _parsedMeshtasticPackets;
    private long _duplicateMeshtasticPackets;
    private long _decodedMeshtasticData;
    private long _nextSequence;
    private long _lastProcessedSequence = -1;
    private int _targetInPassband;
    private int _warmupInProgress;
    private int _acceptingStreamData = 1;
    private int _activeEnqueues;
    private int _drainRequested;
    private int _disposed;
    private long _enqueuedWorkItems;
    private long _completedWorkItems;
    private long _drainTarget;
    private TaskCompletionSource? _drainCompletion;
    private IqSampleRingBuffer? _currentRingBuffer;
    private int _ringGeneration;
    private long _latestAbsoluteSampleEnd;
    private long _deferredRecoveredBlocks;
    private long _expiredHistoryBlocks;
    private LoRaPreambleDetection? _lastDetection;
    private LoRaFrameSynchronization? _lastSynchronization;
    private LoRaExplicitHeader? _lastHeader;
    private LoRaPayloadFrame? _lastPayload;
    private MeshtasticRadioPacket? _lastMeshtasticPacket;
    private MeshtasticData? _lastMeshtasticData;

    public MeshtasticReceiveService()
    {
        _blocks = new BlockingCollection<IqBlock>(_blockQueue, QueueCapacity);
        if (MeshtasticJpLongFastProfile.CalculateChannelFrequencyHz(MeshtasticJpLongFastProfile.Channel) !=
            MeshtasticJpLongFastProfile.FrequencyHz)
        {
            throw new InvalidOperationException("JP LongFast channel profile is internally inconsistent.");
        }

        _worker = Task.Factory.StartNew(
            ProcessBlocks,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public event Action<LoRaPreambleDetection>? PreambleDetected;
    public event Action<LoRaFrameSynchronization>? FrameSynchronized;
    public event Action<LoRaExplicitHeader>? ExplicitHeaderDecoded;
    public event Action<LoRaPayloadFrame>? PayloadDecoded;
    public event Action<LoRaAcquisitionDiagnostic>? AcquisitionDiagnostic;
    public event Action<MeshtasticPacketReception>? MeshtasticPacketReceived;
    public event Action<MeshtasticDataReception>? MeshtasticDataReceived;

    public bool TryConfigureChannels(MeshtasticRegion region, MeshtasticModemPreset preset, IReadOnlyList<MeshtasticBandwidthSlots> bandwidthSlots, out string error)
    {
        MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(preset);
        MeshtasticRegionProfile regionProfile = MeshtasticJpLongFastProfile.GetRegion(region);
        if (bandwidthSlots.Count == 0 || bandwidthSlots.Any(value => value.RadioChannels.Count == 0))
        { error = "各帯域の周波数スロットを1つ以上入力してください。"; return false; }
        ChannelKey[] channelKeys = MeshtasticJpLongFastProfile.GetDetectionProfiles(preset)
            .Select(candidate => candidate.Name)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new ChannelKey(
                name,
                MeshtasticChannelDecryptor.DefaultLongFastKey,
                MeshtasticChannelDecryptor.CalculateChannelHash(name, MeshtasticChannelDecryptor.DefaultLongFastKey)))
            .ToArray();
        int version = Interlocked.Increment(ref _configurationVersion);
        IReadOnlyList<MeshtasticLoRaProfile> channelProfiles = MeshtasticJpLongFastProfile.GetChannelProfiles(preset);
        var configurations = new List<ChannelConfiguration>();
        try
        {
            foreach (MeshtasticBandwidthSlots selection in bandwidthSlots)
            {
                MeshtasticLoRaProfile? channelProfile = channelProfiles.FirstOrDefault(value => value.BandwidthHz == selection.BandwidthHz);
                if (channelProfile is null)
                {
                    error = $"{selection.BandwidthHz / 1000} kHzは選択したプリセットの対象外です。";
                    return false;
                }
                foreach (int channel in selection.RadioChannels.Distinct().Order())
                {
                    int frequencyHz = regionProfile.CalculateChannelFrequencyHz(channel, channelProfile.BandwidthHz);
                    configurations.Add(new ChannelConfiguration(region, channel, frequencyHz, channelProfile, channelKeys, version));
                }
            }
        }
        catch (ArgumentOutOfRangeException exception) { error = exception.Message; return false; }
        Volatile.Write(ref _channelConfigurations, configurations.ToArray());
        string modulation = MeshtasticJpLongFastProfile.IsAutoSf(preset)
            ? $"SF={string.Join(',', MeshtasticJpLongFastProfile.GetDetectionProfiles(preset).Select(candidate => candidate.SpreadingFactor))} CR=header"
            : $"SF={profile.SpreadingFactor} CR=4/{profile.CodingRateDenominator}";
        string slotSummary = string.Join(";", bandwidthSlots.Select(value => $"{value.BandwidthHz / 1000}k:{string.Join(',', value.RadioChannels)}"));
        error = string.Empty;
        return true;
    }

    public MeshtasticReceiveSnapshot Snapshot => new(
        Volatile.Read(ref _targetInPassband) != 0,
        Interlocked.Read(ref _submittedBlocks),
        Interlocked.Read(ref _processedBlocks),
        Interlocked.Read(ref _droppedBlocks),
        _blocks.Count,
        Volatile.Read(ref _maximumQueueDepth),
        Volatile.Read(ref _currentQueueDelayUs) / 1000.0,
        CalculateAverageMilliseconds(_totalQueueDelayUs, _timedBlockCount),
        Volatile.Read(ref _maximumQueueDelayUs) / 1000.0,
        Volatile.Read(ref _currentProcessingTimeUs) / 1000.0,
        CalculateAverageMilliseconds(_totalProcessingTimeUs, _timedBlockCount),
        Volatile.Read(ref _maximumProcessingTimeUs) / 1000.0,
        Volatile.Read(ref _currentInputBlockTimeUs) / 1000.0,
        Volatile.Read(ref _currentProcessingLoadBasisPoints) / 100.0,
        CalculateLoadPercent(_totalProcessingTimeUs, _totalInputBlockTimeUs),
        Volatile.Read(ref _maximumProcessingLoadBasisPoints) / 100.0,
        Interlocked.Read(ref _detectedPreambles),
        Interlocked.Read(ref _synchronizedFrames),
        Interlocked.Read(ref _decodedHeaders),
        Interlocked.Read(ref _decodedPayloads),
        Interlocked.Read(ref _parsedMeshtasticPackets),
        Interlocked.Read(ref _duplicateMeshtasticPackets),
        Interlocked.Read(ref _decodedMeshtasticData),
        _lastDetection,
        _lastSynchronization,
        _lastHeader,
        _lastPayload,
        _lastMeshtasticPacket,
        _lastMeshtasticData,
        CalculateOldestDeferredIqMs(),
        CalculateDeferredRetentionRemainingMs(),
        Interlocked.Read(ref _deferredRecoveredBlocks),
        Interlocked.Read(ref _expiredHistoryBlocks),
        CalculateAverageCpuMilliseconds(_totalChannelizationTicks, _timedBlockCount),
        CalculateAverageCpuMilliseconds(_totalDetectionTicks, _timedBlockCount),
        Volatile.Read(ref _lastInputSampleRateHz));

    private double CalculateOldestDeferredIqMs()
    {
        if (!_blockQueue.TryPeek(out IqBlock? block) || block.RingGeneration != Volatile.Read(ref _ringGeneration)) return 0;
        long ageSamples = Math.Max(0, Volatile.Read(ref _latestAbsoluteSampleEnd) - block.AbsoluteSampleStart);
        return ageSamples * 1000.0 / Math.Max(1, block.SampleRateHz);
    }

    private double CalculateDeferredRetentionRemainingMs()
    {
        if (!_blockQueue.TryPeek(out IqBlock? block) || block.RingGeneration != Volatile.Read(ref _ringGeneration)) return 0;
        long ageSamples = Math.Max(0, Volatile.Read(ref _latestAbsoluteSampleEnd) - block.AbsoluteSampleStart);
        long remaining = Math.Max(0, block.Buffer.Capacity - block.Count * RingSafetyBlocks - ageSamples);
        return remaining * 1000.0 / Math.Max(1, block.SampleRateHz);
    }

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _submittedBlocks, 0);
        Interlocked.Exchange(ref _processedBlocks, 0);
        Interlocked.Exchange(ref _droppedBlocks, 0);
        Interlocked.Exchange(ref _detectedPreambles, 0);
        Interlocked.Exchange(ref _synchronizedFrames, 0);
        Interlocked.Exchange(ref _decodedHeaders, 0);
        Interlocked.Exchange(ref _decodedPayloads, 0);
        Interlocked.Exchange(ref _parsedMeshtasticPackets, 0);
        Interlocked.Exchange(ref _duplicateMeshtasticPackets, 0);
        Interlocked.Exchange(ref _decodedMeshtasticData, 0);
        Interlocked.Exchange(ref _timedBlockCount, 0);
        Interlocked.Exchange(ref _totalQueueDelayUs, 0);
        Interlocked.Exchange(ref _maximumQueueDelayUs, 0);
        Interlocked.Exchange(ref _currentQueueDelayUs, 0);
        Interlocked.Exchange(ref _totalProcessingTimeUs, 0);
        Interlocked.Exchange(ref _maximumProcessingTimeUs, 0);
        Interlocked.Exchange(ref _currentProcessingTimeUs, 0);
        Interlocked.Exchange(ref _totalChannelizationTicks, 0);
        Interlocked.Exchange(ref _totalDetectionTicks, 0);
        Interlocked.Exchange(ref _totalInputBlockTimeUs, 0);
        Interlocked.Exchange(ref _currentInputBlockTimeUs, 0);
        Interlocked.Exchange(ref _currentProcessingLoadBasisPoints, 0);
        Interlocked.Exchange(ref _maximumProcessingLoadBasisPoints, 0);
        Interlocked.Exchange(ref _maximumQueueDepth, 0);
        Interlocked.Exchange(ref _deferredRecoveredBlocks, 0);
        Interlocked.Exchange(ref _expiredHistoryBlocks, 0);
    }

    public bool TrySubmit(
        IqSampleRingBuffer buffer,
        int blockStartPointer,
        int sampleCount,
        int sampleRateHz,
        int inputCenterFrequencyHz,
        long absoluteSampleEnd)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _acceptingStreamData) == 0 ||
            sampleCount <= 0 || sampleRateHz <= 0 || inputCenterFrequencyHz <= 0)
        {
            return false;
        }

        UpdateRingProgress(buffer, absoluteSampleEnd);
        Volatile.Write(ref _lastInputSampleRateHz, sampleRateHz);
        long sequence = Interlocked.Increment(ref _nextSequence);
        ChannelConfiguration[] configurations = Volatile.Read(ref _channelConfigurations);
        bool anyInPassband = configurations.Any(configuration => Math.Abs(configuration.FrequencyHz - inputCenterFrequencyHz) + (configuration.Profile.BandwidthHz / 2.0) <= sampleRateHz * 0.475);
        bool allInPassband = configurations.All(configuration => Math.Abs(configuration.FrequencyHz - inputCenterFrequencyHz) + (configuration.Profile.BandwidthHz / 2.0) <= sampleRateHz * 0.475);
        Volatile.Write(ref _targetInPassband, allInPassband ? 1 : 0);
        if (!anyInPassband) return false;

        int count = Math.Min(sampleCount, buffer.Capacity);
        int generation = Volatile.Read(ref _ringGeneration);
        int safeHistoryBlocks = Math.Max(1, buffer.Capacity / Math.Max(1, count) - RingSafetyBlocks);
        if (_blocks.Count >= Math.Min(QueueCapacity, safeHistoryBlocks))
        {
            RegisterExpiredBlock();
            return false;
        }

        var block = new IqBlock(buffer, blockStartPointer, count, sampleRateHz, inputCenterFrequencyHz,
            sequence, configurations, Stopwatch.GetTimestamp(), absoluteSampleEnd - count, generation);
        return TryEnqueue(block);
    }

    public bool TrySubmitNormalized(
        ReadOnlySpan<Complex32> samples,
        int sampleRateHz,
        int inputCenterFrequencyHz,
        long sequence,
        long absoluteSampleStart)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _acceptingStreamData) == 0 ||
            samples.IsEmpty || sampleRateHz <= 0 || inputCenterFrequencyHz <= 0)
            return false;

        Volatile.Write(ref _lastInputSampleRateHz, sampleRateHz);
        ChannelConfiguration[] configurations = Volatile.Read(ref _channelConfigurations);
        bool anyInPassband = configurations.Any(configuration =>
            Math.Abs(configuration.FrequencyHz - inputCenterFrequencyHz) +
            configuration.Profile.BandwidthHz / 2.0 <= sampleRateHz * 0.475);
        bool allInPassband = configurations.All(configuration =>
            Math.Abs(configuration.FrequencyHz - inputCenterFrequencyHz) +
            configuration.Profile.BandwidthHz / 2.0 <= sampleRateHz * 0.475);
        Volatile.Write(ref _targetInPassband, allInPassband ? 1 : 0);
        if (!anyInPassband) return false;

        short[] samplesI = ArrayPool<short>.Shared.Rent(samples.Length);
        short[] samplesQ = ArrayPool<short>.Shared.Rent(samples.Length);
        for (int index = 0; index < samples.Length; index++)
        {
            samplesI[index] = NormalizeToInt16(samples[index].I);
            samplesQ[index] = NormalizeToInt16(samples[index].Q);
        }

        var block = new IqBlock(
            samplesI,
            samplesQ,
            samples.Length,
            sampleRateHz,
            inputCenterFrequencyHz,
            sequence,
            configurations,
            Stopwatch.GetTimestamp(),
            absoluteSampleStart);
        return TryEnqueue(block);
    }

    private bool TryEnqueue(IqBlock block)
    {
        Interlocked.Increment(ref _activeEnqueues);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _acceptingStreamData) == 0)
            {
                block.Dispose();
                return false;
            }
            if (!_blocks.TryAdd(block))
            {
                block.Dispose();
                RegisterExpiredBlock();
                return false;
            }

            Interlocked.Increment(ref _enqueuedWorkItems);
            Interlocked.Increment(ref _submittedBlocks);
            UpdateMaximum(ref _maximumQueueDepth, _blocks.Count);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _activeEnqueues);
        }
    }

    public ValueTask WarmUpProcessingAsync(
        int sampleRateHz,
        int inputCenterFrequencyHz,
        int blockCount,
        CancellationToken cancellationToken)
    {
        if (sampleRateHz <= 0 || inputCenterFrequencyHz <= 0 || blockCount <= 0)
            return ValueTask.CompletedTask;
        return new ValueTask(Task.Run(() =>
        {
            if (Interlocked.Exchange(ref _warmupInProgress, 1) != 0) return;
            Complex32[]? samples = null;
            bool warmupCompleted = false;
            try
            {
                int sampleCount = Math.Clamp(sampleRateHz / 10, 4_096, 1_000_000);
                samples = ArrayPool<Complex32>.Shared.Rent(sampleCount);
                samples.AsSpan(0, sampleCount).Clear();
                ChannelConfiguration[] configurations =
                    Volatile.Read(ref _channelConfigurations);
                if (configurations.Length == 0) return;

                int minimumFrequency = configurations.Min(configuration =>
                    configuration.FrequencyHz);
                int maximumFrequency = configurations.Max(configuration =>
                    configuration.FrequencyHz);
                int warmupCenterFrequencyHz = checked((int)(
                    ((long)minimumFrequency + maximumFrequency) / 2));
                bool requestedCenterCoversAll = configurations.All(configuration =>
                    Math.Abs(configuration.FrequencyHz - inputCenterFrequencyHz) +
                    configuration.Profile.BandwidthHz / 2.0 <= sampleRateHz * 0.475);
                if (requestedCenterCoversAll)
                    warmupCenterFrequencyHz = inputCenterFrequencyHz;

                ResetStatistics();
                int blocks = Math.Clamp(blockCount, 1, 8);
                for (int block = 0; block < blocks; block++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long expectedProcessed = block + 1;
                    if (!TrySubmitNormalized(
                            samples.AsSpan(0, sampleCount),
                            sampleRateHz,
                            warmupCenterFrequencyHz,
                            long.MinValue + block,
                            (long)block * sampleCount))
                    {
                        throw new InvalidOperationException(
                            "Meshtastic DSP warm-up block could not be queued.");
                    }

                    if (!SpinWait.SpinUntil(
                            () => Interlocked.Read(ref _processedBlocks) >= expectedProcessed,
                            TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException(
                            "Meshtastic DSP warm-up timed out.");
                    }
                }
                warmupCompleted = true;
            }
            finally
            {
                if (warmupCompleted)
                {
                    foreach (ChannelReceiver receiver in _channelReceivers)
                        receiver.Reset();
                    _lastProcessedSequence = -1;
                }
                ResetStatistics();
                Volatile.Write(ref _targetInPassband, 0);
                Volatile.Write(ref _warmupInProgress, 0);
                if (samples is not null)
                    ArrayPool<Complex32>.Shared.Return(samples);
            }
        }, cancellationToken));
    }

    public void UpdateRingProgress(IqSampleRingBuffer buffer, long absoluteSampleEnd)
    {
        if (!ReferenceEquals(buffer, _currentRingBuffer))
        {
            _currentRingBuffer = buffer;
            Interlocked.Increment(ref _ringGeneration);
        }
        Volatile.Write(ref _latestAbsoluteSampleEnd, absoluteSampleEnd);
    }

    private void RegisterExpiredBlock()
    {
        Interlocked.Increment(ref _droppedBlocks);
        Interlocked.Increment(ref _expiredHistoryBlocks);
    }

}
