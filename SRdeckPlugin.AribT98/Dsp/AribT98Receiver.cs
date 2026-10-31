using System.Runtime.CompilerServices;
using SRdeckPlugin.AribT98.Models;
using SRdeckPlugin.AribT98.Protocols;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.AribT98.Dsp;

/// <summary>Streaming 48 kHz channel-IQ receiver. No full-band IQ copying or external process.</summary>
public sealed class AribT98Receiver
{
    public const int SampleRateHz = 48_000;
    public const int SymbolRate = 2_400;
    public const int SamplesPerSymbol = SampleRateHz / SymbolRate;
    private const int FrameSamples = SamplesPerSymbol * AribT98Protocol.SymbolsPerFrame;
    private readonly AribT98ReceiveFilter filter = new();
    private readonly float[] ring = new float[8192];
    private readonly float[] eyePattern = new float[FrameSamples + 1];
    private bool hasEyePattern;
    private readonly Complex32[] discriminatorHistory;
    private int discriminatorIndex, discriminatorSamples;
    private readonly int discriminatorDelay;
    private long position, searchAfter, candidateDue = -1, candidatePosition;
    private double candidateError, candidateOffset, candidateGain;
    private readonly SyncCandidate[] candidates = new SyncCandidate[SamplesPerSymbol + 1];
    private readonly int maximumCandidates;
    private readonly bool fractionalTiming;
    private readonly bool preserveSoftInformation;
    private int candidateCount;
    private AribT98Frame? lastAccepted;
    private readonly record struct SyncCandidate(long Position, double Error, double Offset, double Gain);

    public AribT98Receiver() : this(3, false, true, 5) { }

    // Keep the single-sample discriminator and fractional timing for paired
    // measurements; product settings do not expose experimental modes.
    internal AribT98Receiver(int maximumCandidates, bool fractionalTiming, bool preserveSoftInformation = true,
        int discriminatorLag = 1)
    {
        if (maximumCandidates is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        if (discriminatorLag is not (1 or 5)) throw new ArgumentOutOfRangeException(nameof(discriminatorLag));
        discriminatorHistory = new Complex32[discriminatorLag];
        discriminatorDelay = (discriminatorLag - 1) / 2;
        this.maximumCandidates = maximumCandidates;
        this.fractionalTiming = fractionalTiming;
        this.preserveSoftInformation = preserveSoftInformation;
    }

    public long DecodeAttempts { get; private set; }
    public long RecoveredFrames { get; private set; }
    public int MaximumAttemptsPerFrame { get; private set; }
    public long AcceptedFrames { get; private set; }
    public long RejectedFrames { get; private set; }
    public double InputRms { get; private set; }
    public double FrequencyOffsetHz { get; private set; }
    public double LastSyncError { get; private set; }
    public float[] SnapshotEyePattern() => hasEyePattern ? (float[])eyePattern.Clone() : [];

    public void Reset()
    {
        filter.Reset(); Array.Clear(ring); Array.Clear(discriminatorHistory);
        discriminatorIndex = discriminatorSamples = 0;
        position = searchAfter = 0; candidateDue = -1;
        InputRms = FrequencyOffsetHz = LastSyncError = 0;
        hasEyePattern = false;
        AcceptedFrames = RejectedFrames = 0;
        DecodeAttempts = RecoveredFrames = 0; MaximumAttemptsPerFrame = candidateCount = 0;
        lastAccepted = null;
    }

    public IReadOnlyList<AribT98Frame> Process(ReadOnlySpan<Complex32> samples)
    {
        List<AribT98Frame>? frames = null;
        double power = 0;
        foreach (Complex32 sample in samples)
        {
            double magnitude = (double)sample.I*sample.I + (double)sample.Q*sample.Q;
            if (!double.IsFinite(magnitude))
            {
                filter.Reset(); Array.Clear(ring); Array.Clear(discriminatorHistory);
                discriminatorIndex = discriminatorSamples = 0; candidateDue = -1; candidateCount = 0; lastAccepted = null;
                hasEyePattern = false;
                searchAfter = position + FrameSamples + AribT98ReceiveFilter.DelaySamples;
                position++;
                continue;
            }
            power += magnitude;
            float discriminator = 0;
            if (discriminatorSamples == discriminatorHistory.Length && magnitude > 1e-16)
            {
                Complex32 previous = discriminatorHistory[discriminatorIndex];
                double re = (double)sample.I*previous.I + (double)sample.Q*previous.Q;
                double im = (double)sample.Q*previous.I - (double)sample.I*previous.Q;
                // A five-sample phase difference suppresses fade-induced phase impulses.
                // Its +/-4.8 kHz range covers the host channel filter (stopband at 4 kHz).
                discriminator = (float)(DiscriminatorAngle(im, re) * SampleRateHz /
                    (2*Math.PI*315*discriminatorHistory.Length));
            }
            discriminatorHistory[discriminatorIndex] = sample;
            if (++discriminatorIndex == discriminatorHistory.Length) discriminatorIndex = 0;
            discriminatorSamples = magnitude > 1e-16 ? Math.Min(discriminatorSamples + 1, discriminatorHistory.Length) : 0;
            ProcessDiscriminator(discriminator, ref frames);
        }
        InputRms = samples.IsEmpty ? 0 : Math.Sqrt(power / samples.Length);
        return frames is null ? Array.Empty<AribT98Frame>() : frames;
    }

    private void ProcessDiscriminator(float value, ref List<AribT98Frame>? frames)
    {
        long now = position++;
        ring[now & 8191] = filter.Process(value);
        if (now < FrameSamples + AribT98ReceiveFilter.DelaySamples || now < searchAfter) return;
        long start = now - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol;
        double sum = 0, xy = 0, square = 0;
        for (int i = 0; i < 10; i++)
        {
            double y = ring[(start + i*SamplesPerSymbol) & 8191];
            sum += y; square += y*y; xy += y * AribT98Protocol.SyncSymbols[i];
        }
        // Sync has zero mean and sum(x*x)=74; a signed gain also handles inverted IQ.
        double gain = xy / 74, offset = sum / 10;
        double error = (square - sum*sum/10 - xy*xy/74) / Math.Max(gain*gain*74, 1e-12);
        if (Math.Abs(gain) is >= 0.65 and <= 1.4 && Math.Abs(offset) < 6.35 && error < 0.10)
        {
            if (candidateDue < 0)
            {
                candidateDue = now + SamplesPerSymbol;
                candidateError = double.PositiveInfinity;
                candidateCount = 0;
            }
            if (error < candidateError)
            {
                candidateError = error; candidatePosition = now;
                candidateOffset = offset; candidateGain = gain;
            }
            if (candidateCount < candidates.Length) candidates[candidateCount++] = new(now, error, offset, gain);
        }
        if (candidateDue < 0 || now < candidateDue) return;
        var first = new SyncCandidate(candidatePosition, candidateError, candidateOffset, candidateGain);
        int attempts = 0;
        AribT98Frame? frame = null;
        SyncCandidate accepted = first;
        if (fractionalTiming)
        {
            // Evaluate fractional phase separately from candidate diversity.
            double fraction = RefineTiming(first);
            if (Math.Abs(fraction) > 0.01)
            {
                double refinedStart = first.Position - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol + fraction;
                double refinedError = MeasureSync(refinedStart, out double refinedGain, out double refinedOffset);
                if (Math.Abs(refinedGain) is >= 0.65 and <= 1.4 && Math.Abs(refinedOffset) < 6.35 && refinedError < 0.10)
                {
                    var refined = first with { Error = refinedError, Gain = refinedGain, Offset = refinedOffset };
                    if (TryCandidate(refined, fraction, ref attempts, out frame)) accepted = refined;
                }
            }
        }
        if (frame is null) TryCandidate(first, 0, ref attempts, out frame);
        if (frame is null && maximumCandidates > 1)
        {
            candidates.AsSpan(0, candidateCount).Sort(static (left, right) => left.Error.CompareTo(right.Error));
            Span<long> tried = stackalloc long[3];
            tried[0] = first.Position;
            int count = 1;
            for (int i = 0; i < candidateCount && count < maximumCandidates; i++)
            {
                var next = candidates[i];
                bool distinct = true;
                for (int j = 0; j < count; j++) if (Math.Abs(next.Position - tried[j]) < 2) distinct = false;
                if (!distinct) continue;
                tried[count++] = next.Position;
                if (TryCandidate(next, 0, ref attempts, out frame) && ConsistentRecovery(next, frame!))
                { accepted = next; break; }
                frame = null;
            }
        }
        DecodeAttempts += attempts;
        MaximumAttemptsPerFrame = Math.Max(MaximumAttemptsPerFrame, attempts);
        if (frame is not null)
        {
            AcceptedFrames++;
            if (accepted.Position != first.Position) RecoveredFrames++;
            FrequencyOffsetHz = accepted.Offset * 315;
            LastSyncError = accepted.Error;
            frame.SamplePosition = accepted.Position - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol - AribT98ReceiveFilter.DelaySamples - discriminatorDelay;
            frame.FrequencyOffsetHz = FrequencyOffsetHz;
            frame.SyncError = accepted.Error;
            frame.Inverted = accepted.Gain < 0;
            lastAccepted = frame;
            CaptureEyePattern(accepted);
            (frames ??= []).Add(frame);
            searchAfter = accepted.Position + FrameSamples - 2 * SamplesPerSymbol;
        }
        else { RejectedFrames++; searchAfter = now + SamplesPerSymbol; }
        candidateDue = -1;
        candidateCount = 0;
    }

    private void CaptureEyePattern(SyncCandidate candidate)
    {
        long first = candidate.Position - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol;
        for (int i = 0; i < eyePattern.Length; i++)
            eyePattern[i] = (float)((ring[(first + i) & 8191] - candidate.Offset) / candidate.Gain);
        hasEyePattern = true;
    }

    private bool ConsistentRecovery(SyncCandidate candidate, AribT98Frame frame)
    {
        // CRC-6 alone is too weak to authorize additional hypotheses. Restrict
        // fallback to the next frame of an already validated, unchanged layout
        // and SACCH payload. A new call/layout is acquired by the primary path.
        if (lastAccepted is not { } last) return false;
        long start = candidate.Position - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol - AribT98ReceiveFilter.DelaySamples - discriminatorDelay;
        return start - last.SamplePosition is >= 3700 and <= 3980 &&
            frame.IsCommunication == last.IsCommunication && frame.Mode == last.Mode &&
            frame.ControlBits.AsSpan().SequenceEqual(last.ControlBits);
    }

    private bool TryCandidate(SyncCandidate candidate, double fraction, ref int attempts, out AribT98Frame? frame)
    {
        attempts++;
        Span<float> symbols = stackalloc float[AribT98Protocol.SymbolsPerFrame];
        double start = candidate.Position - (symbols.Length - 1) * SamplesPerSymbol + fraction;
        double gain = candidate.Gain, offset = candidate.Offset;
        double slicingError = 0;
        for (int i = 0; i < symbols.Length; i++)
        {
            float symbol = (float)((SampleAt(start + i * SamplesPerSymbol) - offset) / gain);
            symbols[i] = symbol;
            double nearest = symbol < -2 ? -3 : symbol < 0 ? -1 : symbol < 2 ? 1 : 3;
            slicingError += (symbol-nearest)*(symbol-nearest);
        }
        frame = null;
        return slicingError / symbols.Length < 0.45 && AribT98Protocol.TryDecode(symbols, out frame, preserveSoftInformation);
    }

    private double RefineTiming(SyncCandidate candidate)
    {
        double start = candidate.Position - (AribT98Protocol.SymbolsPerFrame - 1) * SamplesPerSymbol;
        double left = MeasureSync(start - 1, out _, out _), right = MeasureSync(start + 1, out _, out _);
        double curvature = left - 2 * candidate.Error + right;
        return curvature > 1e-12 ? Math.Clamp(0.5 * (left - right) / curvature, -0.5, 0.5) : 0;
    }

    private double MeasureSync(double start, out double gain, out double offset)
    {
        double sum = 0, xy = 0, square = 0;
        for (int i = 0; i < 10; i++)
        {
            double value = SampleAt(start + i * SamplesPerSymbol);
            sum += value; square += value * value; xy += value * AribT98Protocol.SyncSymbols[i];
        }
        gain = xy / 74; offset = sum / 10;
        return (square - sum * sum / 10 - xy * xy / 74) / Math.Max(gain * gain * 74, 1e-12);
    }

    private double SampleAt(double position)
    {
        long at = (long)Math.Floor(position);
        double fraction = position - at;
        double left = ring[at & 8191];
        return fraction == 0 ? left : left + fraction * (ring[(at + 1) & 8191] - left);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static double DiscriminatorAngle(double imaginary, double real)
    {
        if (imaginary == 0)
        {
            if (real < 0 || BitConverter.DoubleToInt64Bits(real) < 0)
                return BitConverter.DoubleToInt64Bits(imaginary) < 0 ? -Math.PI : Math.PI;
            return imaginary;
        }
        if (real == 0) return imaginary > 0 ? Math.PI / 2 : -Math.PI / 2;

        // Reduce atan to [-tan(pi/8), tan(pi/8)] before the degree-15 series.
        // The next-term bound is below 1.6e-8 rad for every finite IQ phase.
        double absoluteReal = Math.Abs(real);
        double absoluteImaginary = Math.Abs(imaginary);
        bool imaginaryDominant = absoluteImaginary > absoluteReal;
        double ratio = imaginaryDominant
            ? absoluteReal / absoluteImaginary
            : absoluteImaginary / absoluteReal;
        double angle;
        if (ratio > 0.41421356237309503)
        {
            double reduced = (ratio - 1) / (ratio + 1);
            angle = Math.PI / 4 + AtanTaylor15(reduced);
        }
        else
        {
            angle = AtanTaylor15(ratio);
        }

        if (imaginaryDominant) angle = Math.PI / 2 - angle;
        if (real < 0) angle = Math.PI - angle;
        return imaginary < 0 ? -angle : angle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double AtanTaylor15(double value)
    {
        double squared = value * value;
        return value * (1 + squared * (-1.0 / 3 + squared * (1.0 / 5 + squared *
            (-1.0 / 7 + squared * (1.0 / 9 + squared * (-1.0 / 11 + squared *
                (1.0 / 13 + squared * (-1.0 / 15))))))));
    }
}
