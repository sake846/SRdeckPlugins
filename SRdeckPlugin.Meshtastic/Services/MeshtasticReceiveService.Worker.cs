using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.DSP;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Services;

public sealed partial class MeshtasticReceiveService
{
    private void ProcessBlocks()
    {
        try
        {
            foreach (IqBlock block in _blocks.GetConsumingEnumerable(_cancellation.Token))
            {
                try
                {
                    using (block)
                    {
                        lock (_streamGate)
                        {
                            long processingStarted = Stopwatch.GetTimestamp();
                            long queueDelayUs = ToMicroseconds(Stopwatch.GetElapsedTime(block.EnqueuedTimestamp, processingStarted));
                            long latestEnd = Volatile.Read(ref _latestAbsoluteSampleEnd);
                            if (!block.IsMaterialized &&
                                (block.RingGeneration != Volatile.Read(ref _ringGeneration) ||
                                !MeshtasticDeferredIqPolicy.IsAvailable(
                                    block.AbsoluteSampleStart, block.Count, latestEnd, block.Buffer.Capacity,
                                    block.Count * RingSafetyBlocks)))
                            {
                                RegisterExpiredBlock();
                                continue;
                            }
                            block.Materialize();
                            if (queueDelayUs > block.Count * 3_000_000L / block.SampleRateHz)
                                Interlocked.Increment(ref _deferredRecoveredBlocks);
                            bool discontinuity = _lastProcessedSequence >= 0 && block.Sequence != _lastProcessedSequence + 1;
                            bool configurationChanged = block.ConfigurationVersion != _lastProcessedConfigurationVersion;
                            if (configurationChanged) RebuildChannelReceivers(block.Configurations);
                            long channelizationTicks = 0;
                            long detectionTicks = 0;
                            if (_channelReceivers.Count == 1)
                            {
                                LoRaChannelizerTiming timing = ProcessChannelReceiver(
                                    _channelReceivers[0], block, discontinuity, configurationChanged);
                                channelizationTicks = timing.ChannelizationTicks;
                                detectionTicks = timing.DetectionTicks;
                            }
                            else
                                Parallel.ForEach(_channelReceivers, receiver =>
                                {
                                    LoRaChannelizerTiming timing = ProcessChannelReceiver(
                                        receiver, block, discontinuity, configurationChanged);
                                    Interlocked.Add(ref channelizationTicks, timing.ChannelizationTicks);
                                    Interlocked.Add(ref detectionTicks, timing.DetectionTicks);
                                });
                            _lastProcessedSequence = block.Sequence;
                            _lastProcessedConfigurationVersion = block.ConfigurationVersion;
                            long processingTimeUs = ToMicroseconds(Stopwatch.GetElapsedTime(processingStarted));
                            long inputBlockTimeUs = Math.Max(1, (long)(block.Count * 1_000_000.0 / block.SampleRateHz));
                            long processingLoadBasisPoints = Math.Max(0, (long)(processingTimeUs * 10_000.0 / inputBlockTimeUs));
                            Volatile.Write(ref _currentQueueDelayUs, queueDelayUs);
                            Interlocked.Add(ref _totalQueueDelayUs, queueDelayUs);
                            UpdateMaximum(ref _maximumQueueDelayUs, queueDelayUs);
                            Volatile.Write(ref _currentProcessingTimeUs, processingTimeUs);
                            Interlocked.Add(ref _totalProcessingTimeUs, processingTimeUs);
                            Interlocked.Add(ref _totalChannelizationTicks, channelizationTicks);
                            Interlocked.Add(ref _totalDetectionTicks, detectionTicks);
                            UpdateMaximum(ref _maximumProcessingTimeUs, processingTimeUs);
                            Volatile.Write(ref _currentInputBlockTimeUs, inputBlockTimeUs);
                            Interlocked.Add(ref _totalInputBlockTimeUs, inputBlockTimeUs);
                            Volatile.Write(ref _currentProcessingLoadBasisPoints, processingLoadBasisPoints);
                            UpdateMaximum(ref _maximumProcessingLoadBasisPoints, processingLoadBasisPoints);
                            Interlocked.Increment(ref _timedBlockCount);
                            Interlocked.Increment(ref _processedBlocks);
                        }
                    }
                }
                finally
                {
                    CompleteWorkItem();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            while (_blocks.TryTake(out IqBlock? block))
            {
                block.Dispose();
                CompleteWorkItem();
            }
        }
    }

    private static LoRaChannelizerTiming ProcessChannelReceiver(ChannelReceiver receiver, IqBlock block,
        bool discontinuity, bool configurationChanged)
    {
        double offsetHz = receiver.CenterFrequencyHz - block.InputCenterFrequencyHz;
        bool inPassband = Math.Abs(offsetHz) + receiver.BandwidthHz / 2.0 <= block.SampleRateHz * 0.475;
        if (!inPassband) { receiver.Reset(); return default; }
        receiver.Channelizer.Configure(block.SampleRateHz, offsetHz, receiver.BandwidthHz);
        if (discontinuity || configurationChanged) receiver.Reset();
        return receiver.Channelizer.Process(
            block.SamplesI.AsSpan(0, block.Count),
            block.SamplesQ.AsSpan(0, block.Count),
            receiver.Detectors);
    }

    private static long ToMicroseconds(TimeSpan duration) => Math.Max(0, (long)(duration.TotalMilliseconds * 1000.0));

    private static short NormalizeToInt16(float value) =>
        (short)Math.Clamp((int)MathF.Round(value * 32768.0f), short.MinValue, short.MaxValue);

    private static double CalculateAverageMilliseconds(long totalMicroseconds, long count)
    {
        long sampleCount = Interlocked.Read(ref count);
        return sampleCount == 0 ? 0 : Interlocked.Read(ref totalMicroseconds) / 1000.0 / sampleCount;
    }

    private static double CalculateAverageCpuMilliseconds(long totalTicks, long count)
    {
        long sampleCount = Interlocked.Read(ref count);
        return sampleCount == 0
            ? 0
            : Interlocked.Read(ref totalTicks) * 1000.0 / Stopwatch.Frequency / sampleCount;
    }

    private static double CalculateLoadPercent(long totalProcessingMicroseconds, long totalInputMicroseconds)
    {
        long input = Interlocked.Read(ref totalInputMicroseconds);
        return input == 0 ? 0 : Interlocked.Read(ref totalProcessingMicroseconds) * 100.0 / input;
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private DetectorReceiver CreateDetectorReceiver(ChannelConfiguration configuration, int channelizerCenterFrequencyHz)
    {
        var state = new DetectorReceptionState();
        var detector = new LoRaPreambleDetector(
            MeshtasticJpLongFastProfile.DecoderSampleRateHz,
            configuration.Profile.BandwidthHz,
            configuration.Profile.SpreadingFactor,
            MeshtasticJpLongFastProfile.SyncWord,
            configuration.FrequencyHz - channelizerCenterFrequencyHz);
        detector.PreambleDetected += value => HandlePreambleDetected(configuration, state, value);
        detector.FrameSynchronized += value => HandleFrameSynchronized(configuration, state, value);
        detector.ExplicitHeaderDecoded += value => HandleExplicitHeaderDecoded(configuration, state, value);
        detector.PayloadDecoded += value => HandlePayloadDecoded(configuration, state, value);
        detector.AcquisitionDiagnostic += value => HandleAcquisitionDiagnostic(configuration, value);
        return new DetectorReceiver(configuration, detector);
    }

    private void RebuildChannelReceivers(ChannelConfiguration[] configurations)
    {
        _channelReceivers.Clear();
        const int sharedBandwidthHz = MeshtasticJpLongFastProfile.BandwidthHz;
        const int sharedHalfBandwidthHz = sharedBandwidthHz / 2;
        foreach (IGrouping<(MeshtasticRegion Region, int CenterFrequencyHz), ChannelConfiguration> group in
                 configurations.GroupBy(configuration =>
                 {
                     MeshtasticRegionProfile region = MeshtasticJpLongFastProfile.GetRegion(configuration.Region);
                     int groupIndex = (configuration.FrequencyHz - region.StartHz) / sharedBandwidthHz;
                     int centerFrequencyHz = region.StartHz + sharedHalfBandwidthHz + (groupIndex * sharedBandwidthHz);
                     return (configuration.Region, centerFrequencyHz);
                 }))
        {
            var detectors = new List<DetectorReceiver>();
            foreach (ChannelConfiguration configuration in group)
            {
                foreach (MeshtasticLoRaProfile detectionProfile in MeshtasticJpLongFastProfile.GetDetectionProfiles(configuration.Profile.Preset))
                {
                    ChannelConfiguration detectorConfiguration = configuration with { Profile = detectionProfile };
                    detectors.Add(CreateDetectorReceiver(detectorConfiguration, group.Key.CenterFrequencyHz));
                }
            }
            _channelReceivers.Add(new ChannelReceiver(
                group.Key.CenterFrequencyHz,
                sharedBandwidthHz,
                new LoRaChannelizer(),
                detectors));
        }
    }

    private sealed class IqBlock : IDisposable
    {
        private int _disposed;

        public IqBlock(IqSampleRingBuffer buffer, int ringOffset, int count, int sampleRateHz,
            int inputCenterFrequencyHz, long sequence, ChannelConfiguration[] configurations,
            long enqueuedTimestamp, long absoluteSampleStart, int ringGeneration)
        {
            Buffer = buffer;
            RingOffset = ringOffset;
            Count = count;
            SampleRateHz = sampleRateHz;
            InputCenterFrequencyHz = inputCenterFrequencyHz;
            Sequence = sequence;
            Configurations = configurations;
            EnqueuedTimestamp = enqueuedTimestamp;
            AbsoluteSampleStart = absoluteSampleStart;
            RingGeneration = ringGeneration;
        }

        public IqBlock(short[] samplesI, short[] samplesQ, int count, int sampleRateHz,
            int inputCenterFrequencyHz, long sequence, ChannelConfiguration[] configurations,
            long enqueuedTimestamp, long absoluteSampleStart)
        {
            Buffer = null!;
            SamplesI = samplesI;
            SamplesQ = samplesQ;
            Count = count;
            SampleRateHz = sampleRateHz;
            InputCenterFrequencyHz = inputCenterFrequencyHz;
            Sequence = sequence;
            Configurations = configurations;
            EnqueuedTimestamp = enqueuedTimestamp;
            AbsoluteSampleStart = absoluteSampleStart;
            RingGeneration = -1;
        }

        public IqSampleRingBuffer Buffer { get; }
        public int RingOffset { get; }
        public short[] SamplesI { get; private set; } = [];
        public short[] SamplesQ { get; private set; } = [];
        public int Count { get; }
        public int SampleRateHz { get; }
        public int InputCenterFrequencyHz { get; }
        public long Sequence { get; }
        public ChannelConfiguration[] Configurations { get; }
        public long EnqueuedTimestamp { get; }
        public long AbsoluteSampleStart { get; }
        public int RingGeneration { get; }
        public int ConfigurationVersion => Configurations[0].Version;
        public bool IsMaterialized => SamplesI.Length != 0;

        public void Materialize()
        {
            if (SamplesI.Length != 0) return;
            SamplesI = ArrayPool<short>.Shared.Rent(Count);
            SamplesQ = ArrayPool<short>.Shared.Rent(Count);
            Buffer.CopyTo(RingOffset, SamplesI, SamplesQ, 0, Count);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (SamplesI.Length != 0) ArrayPool<short>.Shared.Return(SamplesI);
            if (SamplesQ.Length != 0) ArrayPool<short>.Shared.Return(SamplesQ);
        }
    }

    private sealed record DetectorReceiver(ChannelConfiguration Configuration, LoRaPreambleDetector Detector);
    private sealed class DetectorReceptionState
    {
        public LoRaPreambleDetection? LastDetection { get; set; }
        public LoRaFrameSynchronization? LastSynchronization { get; set; }
        public LoRaExplicitHeader? LastHeader { get; set; }
    }

    private sealed record ChannelReceiver(int CenterFrequencyHz, int BandwidthHz, LoRaChannelizer Channelizer, IReadOnlyList<DetectorReceiver> DetectorReceivers)
    {
        public IReadOnlyList<LoRaPreambleDetector> Detectors { get; } = DetectorReceivers.Select(value => value.Detector).ToArray();
        public void Reset()
        {
            Channelizer.Reset();
            foreach (DetectorReceiver receiver in DetectorReceivers) receiver.Detector.Reset();
        }
    }

    private sealed record ChannelKey(string Name, byte[] Key, byte Hash);
    private sealed record ChannelConfiguration(MeshtasticRegion Region, int RadioChannel, int FrequencyHz, MeshtasticLoRaProfile Profile, ChannelKey[] ChannelKeys, int Version)
    {
        public ChannelConfiguration(
            MeshtasticRegion region,
            int radioChannel,
            int frequencyHz,
            MeshtasticLoRaProfile profile,
            string name,
            byte[] key,
            byte hash,
            int version)
            : this(region, radioChannel, frequencyHz, profile, [new ChannelKey(name, key, hash)], version)
        {
        }
    }
}
