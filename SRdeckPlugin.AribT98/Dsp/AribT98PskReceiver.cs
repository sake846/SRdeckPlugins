using SRdeckPlugin.AribT98.Models;
using SRdeckPlugin.AribT98.Protocols;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.AribT98.Dsp;

/// <summary>Part 1 pi/4 DQPSK, 4800 symbol/s, 40 ms. Consumes host-channelized 48 kHz IQ.</summary>
public sealed class AribT98PskReceiver
{
    public const int SampleRateHz = 48_000, SymbolRate = 4_800, SamplesPerSymbol = 10;
    private const int FrameSamples = 1920, Mask = 8191;
    private readonly AribT98PskFilter filter = new();
    private readonly Complex32[] filtered = new Complex32[Mask+1], differential = new Complex32[Mask+1];
    private static readonly Complex32[] ScSync = MakeSync(AribT98PskProtocol.CommunicationSync, 10);
    private static readonly Complex32[] SbSync = MakeSync(AribT98PskProtocol.BurstSync, 16);
    private long position, searchAfter, candidateDue = -1, candidatePosition;
    private double candidateError, candidateStep;
    private bool candidateBurst, candidateInverted;
    public long AcceptedFrames { get; private set; }
    public long RejectedFrames { get; private set; }
    public double InputRms { get; private set; }
    public double FrequencyOffsetHz { get; private set; }
    public double LastSyncError { get; private set; }

    public void Reset()
    {
        filter.Reset(); Array.Clear(filtered); Array.Clear(differential);
        position = searchAfter = 0; candidateDue = -1;
        AcceptedFrames = RejectedFrames = 0;
        InputRms = FrequencyOffsetHz = LastSyncError = 0;
    }

    public IReadOnlyList<AribT98Frame> Process(ReadOnlySpan<Complex32> samples, int? arbitrarySeed = null)
    {
        if (arbitrarySeed is < 0 or > 511) throw new ArgumentOutOfRangeException(nameof(arbitrarySeed));
        List<AribT98Frame>? frames = null;
        double power = 0;
        foreach (Complex32 sample in samples)
        {
            long now = position++;
            double magnitude = (double)sample.I*sample.I + (double)sample.Q*sample.Q;
            if (!double.IsFinite(magnitude))
            {
                filter.Reset(); Array.Clear(filtered); Array.Clear(differential); candidateDue = -1;
                searchAfter = now + FrameSamples + AribT98PskFilter.DelaySamples;
                continue;
            }
            power += magnitude;
            Complex32 current = filter.Process(sample), previous = filtered[(now-SamplesPerSymbol)&Mask];
            filtered[now&Mask] = current;
            double re = (double)current.I*previous.I + (double)current.Q*previous.Q;
            double im = (double)current.Q*previous.I - (double)current.I*previous.Q;
            double norm = Math.Sqrt(re*re+im*im);
            differential[now&Mask] = norm > 1e-12 ? new((float)(re/norm), (float)(im/norm)) : default;
            if (now < FrameSamples+AribT98PskFilter.DelaySamples || now < searchAfter) continue;
            long start = now - 191*SamplesPerSymbol;
            MeasureSync(start, ScSync, false, false, now);
            MeasureSync(start, ScSync, false, true, now);
            MeasureSync(start, SbSync, true, false, now);
            MeasureSync(start, SbSync, true, true, now);
            if (candidateDue < 0 || now < candidateDue) continue;
            DecodeCandidate(arbitrarySeed, ref frames);
            candidateDue = -1;
        }
        InputRms = samples.IsEmpty ? 0 : Math.Sqrt(power/samples.Length);
        return frames is null ? Array.Empty<AribT98Frame>() : frames;
    }

    private void MeasureSync(long start, Complex32[] sync, bool burst, bool inverted, long now)
    {
        double re = 0, im = 0;
        for (int i = 0; i < sync.Length; i++)
        {
            Complex32 d = differential[(start+(92+i)*SamplesPerSymbol)&Mask];
            double q = inverted ? -d.Q : d.Q;
            re += d.I*sync[i].I + q*sync[i].Q;
            im += q*sync[i].I - d.I*sync[i].Q;
        }
        double error = Math.Max(0, 1-Math.Sqrt(re*re+im*im)/sync.Length);
        if (error > 0.04) return;
        double step = Math.Atan2(im, re);
        if (Math.Abs(step) > 2*Math.PI*900/SymbolRate) return;
        if (candidateDue < 0) { candidateDue = now+SamplesPerSymbol; candidateError = double.PositiveInfinity; }
        if (error >= candidateError) return;
        candidatePosition = start; candidateError = error; candidateStep = step;
        candidateBurst = burst; candidateInverted = inverted;
    }

    private void DecodeCandidate(int? arbitrarySeed, ref List<AribT98Frame>? frames)
    {
        Span<float> bits = stackalloc float[384];
        double cos = Math.Cos(candidateStep), sin = Math.Sin(candidateStep), error = 0;
        for (int i = 0; i < 192; i++)
        {
            Complex32 d = differential[(candidatePosition+i*SamplesPerSymbol)&Mask];
            double q = candidateInverted ? -d.Q : d.Q;
            double re = d.I*cos+q*sin, im = q*cos-d.I*sin;
            bits[i*2] = (float)-im; bits[i*2+1] = (float)-re;
            // LP+R and guard need not contain valid modulated symbols.
            if (i >= 20 && i < 188) error += 1-(Math.Abs(re)+Math.Abs(im))/Math.Sqrt(2);
        }
        if (error/168 < 0.12 && AribT98PskProtocol.TryDecode(bits, candidateBurst, arbitrarySeed, out var frame))
        {
            AcceptedFrames++;
            // Report CFO in the original input's spectral orientation.
            FrequencyOffsetHz = candidateStep*SymbolRate/(2*Math.PI)*(candidateInverted ? -1 : 1);
            LastSyncError = candidateError;
            frame!.SamplePosition = candidatePosition-AribT98PskFilter.DelaySamples;
            frame.FrequencyOffsetHz = FrequencyOffsetHz;
            frame.SyncError = candidateError;
            frame.Inverted = candidateInverted;
            (frames ??= []).Add(frame);
            searchAfter = candidatePosition+191*SamplesPerSymbol+FrameSamples-2*SamplesPerSymbol;
        }
        else { RejectedFrames++; searchAfter = position+SamplesPerSymbol; }
    }

    private static Complex32[] MakeSync(uint word, int symbols)
    {
        var sync = new Complex32[symbols];
        for (int i = 0; i < symbols; i++)
        {
            double phase = AribT98PskProtocol.DibitPhase((int)(word >> (2*(symbols-1-i))) & 3);
            sync[i] = new((float)Math.Cos(phase), (float)Math.Sin(phase));
        }
        return sync;
    }
}

/// <summary>Energy-normalized RRC, alpha .2, 16 symbols, group delay 80 samples (Part 1 section 3.4).</summary>
internal sealed class AribT98PskFilter
{
    public const int DelaySamples = 80;
    private static readonly double[] Taps = CreateTaps();
    private static readonly double[] ReversedTaps = AribT98FirKernel.Reverse(Taps);
    private readonly double[] historyI = new double[Taps.Length * 2];
    private readonly double[] historyQ = new double[Taps.Length * 2];
    private int position;

    public Complex32 Process(Complex32 sample)
    {
        int taps = Taps.Length;
        historyI[position] = historyI[position + taps] = sample.I;
        historyQ[position] = historyQ[position + taps] = sample.Q;
        double re = AribT98FirKernel.Dot(historyI.AsSpan(position + 1, taps), ReversedTaps);
        double im = AribT98FirKernel.Dot(historyQ.AsSpan(position + 1, taps), ReversedTaps);
        if (++position == taps) position = 0;
        return new((float)re, (float)im);
    }
    public void Reset() { Array.Clear(historyI); Array.Clear(historyQ); position = 0; }

    private static double[] CreateTaps()
    {
        const double alpha = .2;
        var taps = new double[2*DelaySamples+1];
        double energy = 0;
        for (int i = 0; i < taps.Length; i++)
        {
            double t = (i-DelaySamples)/10.0;
            double v = Math.Abs(t) < 1e-12 ? 1+alpha*(4/Math.PI-1) :
                Math.Abs(Math.Abs(t)-1/(4*alpha)) < 1e-12 ? alpha/Math.Sqrt(2)*
                    ((1+2/Math.PI)*Math.Sin(Math.PI/(4*alpha))+(1-2/Math.PI)*Math.Cos(Math.PI/(4*alpha))) :
                (Math.Sin(Math.PI*t*(1-alpha))+4*alpha*t*Math.Cos(Math.PI*t*(1+alpha)))/
                    (Math.PI*t*(1-16*alpha*alpha*t*t));
            taps[i] = v; energy += v*v;
        }
        for (int i = 0; i < taps.Length; i++) taps[i] /= Math.Sqrt(energy);
        return taps;
    }
}
