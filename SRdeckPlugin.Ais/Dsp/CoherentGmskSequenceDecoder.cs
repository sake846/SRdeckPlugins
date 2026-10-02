using System.Runtime.CompilerServices;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Ais.Dsp;

internal readonly record struct CoherentGmskMixedSample(
    double PositiveI,
    double PositiveQ,
    double NegativeI,
    double NegativeQ);

/// <summary>Frequency-only front end shared by all timing hypotheses.</summary>
internal sealed class CoherentGmskFrequencyMixer
{
    // All supported tone hypotheses are integer multiples of 300 Hz, so their
    // continuous local oscillators repeat after 320 samples at 96 kS/s.
    private const int OscillatorPeriod = AisReceiver.DemodulationSampleRateHz / 300;
    private readonly float[] positiveOscillatorI = new float[OscillatorPeriod];
    private readonly float[] positiveOscillatorQ = new float[OscillatorPeriod];
    private readonly float[] negativeOscillatorI = new float[OscillatorPeriod];
    private readonly float[] negativeOscillatorQ = new float[OscillatorPeriod];

    public CoherentGmskFrequencyMixer(int frequencyOffsetHz)
    {
        CreateOscillator(2_400 + frequencyOffsetHz, positiveOscillatorI, positiveOscillatorQ);
        CreateOscillator(-2_400 + frequencyOffsetHz, negativeOscillatorI, negativeOscillatorQ);
    }

    public CoherentGmskMixedSample Mix(Complex32 sample, long sampleIndex)
    {
        int index = (int)(sampleIndex % OscillatorPeriod);
        float positiveI = sample.I * positiveOscillatorI[index] + sample.Q * positiveOscillatorQ[index];
        float positiveQ = sample.Q * positiveOscillatorI[index] - sample.I * positiveOscillatorQ[index];
        float negativeI = sample.I * negativeOscillatorI[index] + sample.Q * negativeOscillatorQ[index];
        float negativeQ = sample.Q * negativeOscillatorI[index] - sample.I * negativeOscillatorQ[index];
        return new(positiveI, positiveQ, negativeI, negativeQ);
    }

    private static void CreateOscillator(int frequencyHz, float[] i, float[] q)
    {
        double step = 2 * Math.PI * frequencyHz / AisReceiver.DemodulationSampleRateHz;
        for (int index = 0; index < i.Length; index++)
        {
            double phase = step * index;
            i[index] = (float)Math.Cos(phase);
            q[index] = (float)Math.Sin(phase);
        }
    }
}

/// <summary>
/// Coherent soft-decision sequence detector for the AIS GMSK waveform.
/// It combines a bank of complex matched correlators with a Viterbi detector
/// whose transition metric enforces the continuous MSK phase trajectory.
/// </summary>
internal sealed class CoherentGmskSequenceDecoder
{
    private const int TracebackDepth = 8;
    private const double ToneRadiansPerSymbol = Math.PI / 2;
    private readonly int offset;
    private readonly int frequencyOffsetHz;
    private readonly int frequencyHypothesisIndex;
    private readonly AisHdlcDecoder decoder = new();
    private readonly double[] confidenceHistory = new double[TracebackDepth];
    private readonly (double Cos, double Sin) positiveTransition;
    private readonly (double Cos, double Sin) negativeTransition;
    private readonly (double Cos, double Sin) switchingTransition;
    private int sampleCount;
    private double positiveI;
    private double positiveQ;
    private double negativeI;
    private double negativeQ;
    private long symbolIndex;
    private double positiveScore;
    private double negativeScore;
    private uint positivePath = 1;
    private uint negativePath;
    private Correlation currentCenter;
    private Correlation previousCenter;
    private bool hasPreviousCorrelations;
    private double recentConfidence;

    public CoherentGmskSequenceDecoder(int offset, int frequencyOffsetHz, int frequencyHypothesisIndex)
    {
        this.offset = offset;
        this.frequencyOffsetHz = frequencyOffsetHz;
        this.frequencyHypothesisIndex = frequencyHypothesisIndex;
        double frequencyPhase = 2 * Math.PI * frequencyOffsetHz / AisReceiver.SymbolRate;
        double positivePhase = ToneRadiansPerSymbol + frequencyPhase;
        double negativePhase = -ToneRadiansPerSymbol + frequencyPhase;
        positiveTransition = (Math.Cos(positivePhase), Math.Sin(positivePhase));
        negativeTransition = (Math.Cos(negativePhase), Math.Sin(negativePhase));
        switchingTransition = (Math.Cos(frequencyPhase), Math.Sin(frequencyPhase));
    }

    public int FrequencyOffsetHz => frequencyOffsetHz;
    public int FrequencyHypothesisIndex => frequencyHypothesisIndex;
    public long RejectedFrames => decoder.RejectedFrames;
    public long FlagCount => decoder.FlagCount;
    public long FrameCandidateCount => decoder.FrameCandidateCount;
    public long ValidFrames => decoder.ValidFrames;

    public void Reset()
    {
        decoder.Reset();
        ResetSequenceState();
    }

    public void ResetState()
    {
        decoder.ResetState();
        ResetSequenceState();
    }

    private void ResetSequenceState()
    {
        sampleCount = 0;
        positiveI = positiveQ = negativeI = negativeQ = 0;
        symbolIndex = 0;
        positiveScore = negativeScore = 0;
        positivePath = 1;
        negativePath = 0;
        currentCenter = previousCenter = default;
        hasPreviousCorrelations = false;
        recentConfidence = 0;
        Array.Clear(confidenceHistory);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[]? Feed(
        Complex32 sample,
        in CoherentGmskMixedSample mixed,
        long sampleIndex,
        out double quality)
    {
        quality = 0;
        if (sampleIndex < offset) return null;

        Accumulate(sample, mixed, sampleCount);
        if (++sampleCount < AisReceiver.SamplesPerSymbol) return null;
        sampleCount = 0;
        return DecodeSymbol(out quality);
    }

    private byte[]? DecodeSymbol(out double quality)
    {
        quality = 0;
        var positive = new Correlation(positiveI, positiveQ);
        var negative = new Correlation(negativeI, negativeQ);
        Correlation center = currentCenter;
        positiveI = positiveQ = negativeI = negativeQ = 0;
        currentCenter = default;

        double totalPower = positive.Power + negative.Power + 1e-20;
        double confidence = Math.Abs(positive.Power - negative.Power) / totalPower;
        confidenceHistory[(int)(symbolIndex & (TracebackDepth - 1))] = confidence;
        double signedEvidence = (positive.Power - negative.Power) / totalPower;
        double positiveEmission = 0.55 * signedEvidence;
        double negativeEmission = -positiveEmission;

        if (!hasPreviousCorrelations)
        {
            positiveScore = positiveEmission;
            negativeScore = negativeEmission;
            previousCenter = center;
            hasPreviousCorrelations = true;
            symbolIndex++;
            return null;
        }

        PhaseDelta(previousCenter, center, out double observedCos, out double observedSin);
        double switchingScore = PhaseScore(observedCos, observedSin, switchingTransition);
        double positiveFromPositive = positiveScore + PhaseScore(observedCos, observedSin, positiveTransition);
        double positiveFromNegative = negativeScore + switchingScore;
        double negativeFromPositive = positiveScore + switchingScore;
        double negativeFromNegative = negativeScore + PhaseScore(observedCos, observedSin, negativeTransition);
        // Carry the survivor bits with each state instead of tracing eight
        // predecessor rows for every symbol. Ties still select the positive state.
        uint nextPositivePath = ((positiveFromNegative > positiveFromPositive
            ? negativePath : positivePath) << 1) | 1;
        negativePath = (negativeFromNegative > negativeFromPositive
            ? negativePath : positivePath) << 1;
        positivePath = nextPositivePath;
        positiveScore = Math.Max(positiveFromPositive, positiveFromNegative) + positiveEmission;
        negativeScore = Math.Max(negativeFromPositive, negativeFromNegative) + negativeEmission;
        double normalization = Math.Max(positiveScore, negativeScore);
        positiveScore -= normalization;
        negativeScore -= normalization;
        previousCenter = center;

        byte[]? payload = null;
        if (symbolIndex >= TracebackDepth)
        {
            uint path = positiveScore >= negativeScore ? positivePath : negativePath;
            int decidedSlot = (int)((symbolIndex - TracebackDepth) & (TracebackDepth - 1));
            recentConfidence = recentConfidence == 0
                ? confidenceHistory[decidedSlot]
                : recentConfidence * 0.94 + confidenceHistory[decidedSlot] * 0.06;
            quality = Math.Clamp(recentConfidence, 0, 1);
            payload = decoder.FeedLevel((path & (1U << TracebackDepth)) != 0);
        }
        symbolIndex++;
        return payload;
    }

    private void Accumulate(Complex32 sample, in CoherentGmskMixedSample mixed, int index)
    {
        positiveI += mixed.PositiveI;
        positiveQ += mixed.PositiveQ;
        negativeI += mixed.NegativeI;
        negativeQ += mixed.NegativeQ;
        if (index is >= 4 and <= 6)
        {
            currentCenter = new(currentCenter.I + sample.I, currentCenter.Q + sample.Q);
        }
    }

    private static void PhaseDelta(
        Correlation previous,
        Correlation current,
        out double cosine,
        out double sine)
    {
        double magnitude = Math.Sqrt(previous.Power * current.Power);
        if (magnitude < 1e-20)
        {
            cosine = sine = 0;
            return;
        }
        double cross = previous.I * current.Q - previous.Q * current.I;
        double dot = previous.I * current.I + previous.Q * current.Q;
        cosine = dot / magnitude;
        sine = cross / magnitude;
    }

    private static double PhaseScore(double observedCos, double observedSin,
        (double Cos, double Sin) transition) =>
        1.15 * Math.Clamp(
            observedCos * transition.Cos + observedSin * transition.Sin, -1, 1);

    private readonly record struct Correlation(double I, double Q)
    {
        public double Power => I * I + Q * Q;
    }
}
