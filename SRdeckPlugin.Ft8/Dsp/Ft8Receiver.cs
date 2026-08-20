using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckCore.SignalProcessing;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.Ft8.Dsp;

/// <summary>UTC-slot aligned streaming front end for the FT8 decoder.</summary>
public sealed class Ft8Receiver
{
    public const int OutputSampleRateHz = Ft8Decoder.SampleRateHz;
    public const int AudioCenterHz = Ft8Decoder.AudioCenterHz;
    public const int OccupiedPassbandHz = 3_200;
    private readonly object gate = new();
    private readonly object diagnosticsGate = new();
    private readonly Ft8Decoder decoder = new();
    private readonly Ft4Decoder ft4Decoder = new();
    private readonly Jt65Decoder jt65Decoder = new();
    private readonly ComplexFrequencyTranslator translator = new();
    private readonly PolyphaseRationalResampler resampler = new(40, 320, true);
    private Complex32[] slot = new Complex32[Ft8Decoder.SlotSamples];
    private WeakSignalMode activeMode = WeakSignalMode.FT8;
    private DateTimeOffset? slotStart;
    private Guid streamId;
    private long channelCenterHz;
    private int lowestWritten = Ft8Decoder.SlotSamples;
    private int highestWritten;
    private int inputSampleRate;
    private double rateConversionIntermediateSampleRate;
    private bool usesHostChannelRateConversion;
    private bool resamplerConfigured;
    private bool channelTimelineConfigured;
    private Guid channelTimelineStreamId;
    private long channelTimelineSourceOrigin;
    private int channelTimelineSourceRate;
    private DateTimeOffset channelTimelineUtcOrigin;
    private long slotsProcessed;
    private long candidatesExamined;
    private long validMessages;
    private long ldpcRejected;
    private long crcRejected;
    private TimeSpan lastDecodeDuration;
    private int lastSlotValidMessages;
    private DateTimeOffset? lastDecodedSlotStart;
    private Task pendingDecode = Task.CompletedTask;
    private Ft8DecoderDiagnostics diagnosticsSnapshot;
    private double monitorOscillatorPhase;
    private float monitorPower;
    private float monitorGain = 1f;
    private double channelPower;
    private static readonly float MonitorPowerCoefficient =
        (float)(1 - Math.Exp(-1 / (0.050 * OutputSampleRateHz)));
    private static readonly float MonitorGainCoefficient =
        (float)(1 - Math.Exp(-1 / (0.020 * OutputSampleRateHz)));

    public event EventHandler<IReadOnlyList<Ft8Reception>>? MessagesDecoded;

    public Ft8DecoderDiagnostics Diagnostics
    {
        get
        {
            // UI diagnostics must never wait for sample processing. In particular,
            // a malformed or driver-delayed block must not be able to hold WPF's
            // dispatcher behind the receiver state lock.
            lock (diagnosticsGate) return diagnosticsSnapshot;
        }
    }

    public void ProcessChannel(ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata, Ft8Settings settings)
        => ProcessChannel(samples, metadata, settings, Span<float>.Empty, out _);

    public void ProcessChannel(ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata, Ft8Settings settings,
        Span<float> monitorAudio, out int monitorAudioSampleCount)
    {
        if (metadata.Configuration.OutputSampleRateHz != OutputSampleRateHz)
            throw new InvalidOperationException($"Weak-signal channel output must be {OutputSampleRateHz} S/s.");
        lock (gate)
        {
            inputSampleRate = metadata.Source.SampleRateHz;
            usesHostChannelRateConversion = true;
            rateConversionIntermediateSampleRate = metadata.Configuration.InputSampleRateHz /
                (double)Math.Max(1, metadata.Configuration.CoarseDecimationFactor) /
                Math.Max(1, metadata.Configuration.FineDecimationFactor);
            DateTimeOffset timestamp = GetChannelSampleTimestamp(metadata);
            if (metadata.Source.Discontinuity != IqDiscontinuity.None) ResetSlotState();
            Feed(samples, timestamp, metadata.Source.StreamId,
                metadata.Configuration.ChannelCenterFrequencyHz, settings);
            monitorAudioSampleCount = RenderMonitorAudio(samples, monitorAudio);
            UpdateDiagnosticsSnapshot();
        }
    }

    public void ProcessRaw(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata,
        long targetCenterHz, Ft8Settings settings)
        => ProcessRaw(samples, metadata, targetCenterHz, settings, Span<float>.Empty, out _);

    public void ProcessRaw(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata,
        long targetCenterHz, Ft8Settings settings,
        Span<float> monitorAudio, out int monitorAudioSampleCount)
    {
        if (metadata.SampleRateHz < OutputSampleRateHz)
            throw new InvalidOperationException("Weak-signal reception requires at least 12.8 kS/s.");
        // Cast before multiplication: at common SDR block sizes a 6 MS/s stream
        // overflows a 32-bit intermediate (for example 262,144 * 12,800).
        // List then receives a negative capacity and faults the entire plugin.
        int convertedCapacity = checked((int)Math.Ceiling(samples.Length *
            (double)OutputSampleRateHz / metadata.SampleRateHz) + 4);
        var converted = new List<Complex32>(convertedCapacity);
        lock (gate)
        {
            inputSampleRate = metadata.SampleRateHz;
            usesHostChannelRateConversion = false;
            rateConversionIntermediateSampleRate = 0;
            if (!resamplerConfigured || metadata.Discontinuity != IqDiscontinuity.None)
            {
                resampler.Configure(metadata.SampleRateHz, 1, OutputSampleRateHz, 3_100);
                resamplerConfigured = true;
                ResetSlotState();
            }
            translator.Configure(targetCenterHz - metadata.CenterFrequencyHz, metadata.SampleRateHz);
            foreach (Complex32 sample in samples)
            {
                translator.Mix(sample.I, sample.Q, out float mixedI, out float mixedQ);
                resampler.Process(mixedI, mixedQ,
                    (i, q) => converted.Add(new Complex32(i, q)));
            }
            DateTimeOffset timestamp = metadata.UtcTimestamp == default
                ? DateTimeOffset.UtcNow : metadata.UtcTimestamp;
            Feed(CollectionsMarshal.AsSpan(converted), timestamp, metadata.StreamId,
                targetCenterHz, settings);
            monitorAudioSampleCount = RenderMonitorAudio(
                CollectionsMarshal.AsSpan(converted), monitorAudio);
            UpdateDiagnosticsSnapshot();
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            ResetSlotState();
            resamplerConfigured = false;
            channelTimelineConfigured = false;
            inputSampleRate = 0;
            rateConversionIntermediateSampleRate = 0;
            usesHostChannelRateConversion = false;
            monitorOscillatorPhase = 0;
            monitorPower = 0;
            monitorGain = 1;
            channelPower = 0;
            UpdateDiagnosticsSnapshot();
        }
    }

    public Task DrainAsync() => pendingDecode;

    private int RenderMonitorAudio(ReadOnlySpan<Complex32> samples, Span<float> output)
    {
        if (output.IsEmpty) return 0;
        if (output.Length < samples.Length)
            throw new ArgumentException("The FT8 monitor audio buffer is too small.", nameof(output));
        double phase = monitorOscillatorPhase;
        double step = 2 * Math.PI * AudioCenterHz / OutputSampleRateHz;
        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 sample = samples[index];
            float raw = sample.I * (float)Math.Cos(phase) -
                        sample.Q * (float)Math.Sin(phase);
            phase += step;
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;

            monitorPower += MonitorPowerCoefficient * (raw * raw - monitorPower);
            float desiredGain = 0.2f / MathF.Sqrt(MathF.Max(monitorPower, 1e-10f));
            desiredGain = Math.Clamp(desiredGain, 0.1f, 40f);
            monitorGain += MonitorGainCoefficient * (desiredGain - monitorGain);
            output[index] = MathF.Tanh(raw * monitorGain);
        }
        monitorOscillatorPhase = phase;
        return samples.Length;
    }

    private void Feed(ReadOnlySpan<Complex32> samples, DateTimeOffset timestamp,
        Guid newStreamId, long newChannelCenterHz, Ft8Settings settings)
    {
        if (samples.IsEmpty) return;
        double blockPower = 0;
        int finiteSamples = 0;
        foreach (Complex32 sample in samples)
        {
            double power = sample.I * sample.I + sample.Q * sample.Q;
            if (!double.IsFinite(power)) continue;
            blockPower += power;
            finiteSamples++;
        }
        if (finiteSamples > 0)
        {
            blockPower /= finiteSamples;
            channelPower = channelPower <= 0 ? blockPower : channelPower + 0.1 * (blockPower - channelPower);
        }
        if (activeMode != settings.Mode)
        {
            activeMode = settings.Mode;
            slot = new Complex32[SlotSamples(activeMode)];
            ResetSlotState();
        }
        DateTimeOffset blockSlot = FloorSlot(timestamp, activeMode);
        if (slotStart is null || streamId != newStreamId || channelCenterHz != newChannelCenterHz)
        {
            slotStart = blockSlot;
            streamId = newStreamId;
            channelCenterHz = newChannelCenterHz;
            lowestWritten = slot.Length;
            highestWritten = 0;
            Array.Clear(slot);
        }

        int source = 0;
        DateTimeOffset sampleTime = timestamp;
        while (source < samples.Length)
        {
            DateTimeOffset targetSlot = FloorSlot(sampleTime, activeMode);
            if (targetSlot != slotStart)
            {
                CompleteSlot(settings);
                slotStart = targetSlot;
                lowestWritten = slot.Length;
                highestWritten = 0;
                Array.Clear(slot);
            }
            long offsetTicks = (sampleTime - slotStart!.Value).Ticks;
            long destinationLong = offsetTicks * OutputSampleRateHz /
                                   TimeSpan.TicksPerSecond;
            // Math.Round could produce slot.Length during the final half-sample
            // before a 15-second boundary. The zero-length copy path then moved
            // slotStart forward without advancing source, and the next iteration
            // moved it back again forever. Integer division floors the position;
            // clamping to the final valid element is a defensive backstop.
            int destination = (int)Math.Clamp(destinationLong, 0, slot.Length - 1L);
            int count = Math.Min(samples.Length - source, slot.Length - destination);
            samples.Slice(source, count).CopyTo(slot.AsSpan(destination));
            lowestWritten = Math.Min(lowestWritten, destination);
            highestWritten = Math.Max(highestWritten, destination + count);
            source += count;
            sampleTime = timestamp.AddSeconds(source / (double)OutputSampleRateHz);
        }
    }

    private void CompleteSlot(Ft8Settings settings)
    {
        int minimumSamples = activeMode switch
        {
            WeakSignalMode.FT4 => OutputSampleRateHz * 6,
            WeakSignalMode.JT65 => OutputSampleRateHz * 48,
            _ => OutputSampleRateHz * 13
        };
        if (slotStart is null || highestWritten < minimumSamples) return;
        // A receiver started part-way through a JT65 minute leaves the missing
        // leading samples as zeroes. A strong carrier after that artificial edge
        // can look like the 126-symbol sync pattern; the hard decisions then form
        // valid all-zero RS codewords and publish garbage such as 000AAA. Wait for
        // the next minute unless reception began within one JT65 symbol of the
        // slot boundary.
        int maximumJt65LeadingGap = (int)Math.Ceiling(
            Jt65Decoder.SymbolDurationSeconds * OutputSampleRateHz);
        if (activeMode == WeakSignalMode.JT65 && lowestWritten > maximumJt65LeadingGap)
            return;
        Complex32[] snapshot = slot.ToArray();
        DateTimeOffset decodeSlotStart = slotStart.Value;
        Guid decodeStreamId = streamId;
        long decodeCenter = channelCenterHz;
        WeakSignalMode decodeMode = activeMode;
        pendingDecode = pendingDecode.ContinueWith(_ =>
        {
            Ft8Decoder.DecodeBatch batch = decodeMode switch
            {
                WeakSignalMode.FT4 => ft4Decoder.DecodeSlot(snapshot, decodeSlotStart,
                    decodeStreamId, decodeCenter, settings),
                WeakSignalMode.JT65 => jt65Decoder.DecodeSlot(snapshot, decodeSlotStart,
                    decodeStreamId, decodeCenter, settings),
                _ => decoder.DecodeSlot(snapshot, decodeSlotStart,
                    decodeStreamId, decodeCenter, settings)
            };
            lock (gate)
            {
                slotsProcessed++;
                candidatesExamined += batch.Candidates;
                validMessages += batch.Messages.Count;
                ldpcRejected += batch.LdpcRejected;
                crcRejected += batch.CrcRejected;
                lastDecodeDuration = batch.Duration;
                lastSlotValidMessages = batch.Messages.Count;
                lastDecodedSlotStart = decodeSlotStart;
                UpdateDiagnosticsSnapshot();
            }
            if (batch.Messages.Count > 0) MessagesDecoded?.Invoke(this, batch.Messages);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void ResetSlotState()
    {
        slotStart = null;
        lowestWritten = slot.Length;
        highestWritten = 0;
        Array.Clear(slot);
    }

    /// <summary>
    /// Reconstructs the UTC time of the first channel sample from its monotonic
    /// source position. Driver timestamps can jitter by a few milliseconds from
    /// block to block; using each one directly creates gaps or overlaps in a
    /// 15-second FT8 slot even though the IQ stream itself is contiguous.
    /// </summary>
    private DateTimeOffset GetChannelSampleTimestamp(ChannelIqBlockMetadata metadata)
    {
        IqBlockMetadata source = metadata.Source;
        bool reset = !channelTimelineConfigured ||
                     channelTimelineStreamId != source.StreamId ||
                     channelTimelineSourceRate != source.SampleRateHz ||
                     channelTimelineSourceOrigin != metadata.SourceSampleOrigin ||
                     source.Discontinuity != IqDiscontinuity.None;
        if (reset)
        {
            channelTimelineConfigured = true;
            channelTimelineStreamId = source.StreamId;
            channelTimelineSourceOrigin = metadata.SourceSampleOrigin;
            channelTimelineSourceRate = source.SampleRateHz;
            channelTimelineUtcOrigin = source.UtcTimestamp == default
                ? DateTimeOffset.UtcNow : source.UtcTimestamp;
        }

        if (channelTimelineSourceRate <= 0)
            return source.UtcTimestamp == default ? DateTimeOffset.UtcNow : source.UtcTimestamp;

        long sourcePosition = metadata.MapOutputToSource(metadata.OutputSampleStart);
        return channelTimelineUtcOrigin.AddSeconds((sourcePosition - channelTimelineSourceOrigin) /
            (double)channelTimelineSourceRate);
    }

    private void UpdateDiagnosticsSnapshot()
    {
        var snapshot = new Ft8DecoderDiagnostics(
            inputSampleRate, highestWritten, slotsProcessed, candidatesExamined,
            validMessages, ldpcRejected, crcRejected, lastDecodeDuration, slotStart,
            lastSlotValidMessages, lastDecodedSlotStart,
            rateConversionIntermediateSampleRate, usesHostChannelRateConversion,
            channelPower > 0 ? 10 * Math.Log10(channelPower) : double.NegativeInfinity);
        lock (diagnosticsGate) diagnosticsSnapshot = snapshot;
    }

    internal static int SlotSamples(WeakSignalMode mode) => mode switch
    {
        WeakSignalMode.FT4 => OutputSampleRateHz * 15 / 2,
        WeakSignalMode.JT65 => OutputSampleRateHz * 60,
        _ => Ft8Decoder.SlotSamples
    };

    private static DateTimeOffset FloorSlot(DateTimeOffset value, WeakSignalMode mode)
    {
        long slotTicks = mode switch
        {
            WeakSignalMode.FT4 => TimeSpan.FromMilliseconds(7_500).Ticks,
            WeakSignalMode.JT65 => TimeSpan.FromSeconds(60).Ticks,
            _ => TimeSpan.FromSeconds(15).Ticks
        };
        long utcTicks = value.UtcTicks / slotTicks * slotTicks;
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }
}
