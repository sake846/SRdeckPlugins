using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Ais.Dsp;

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
    private readonly AisHdlcDecoder decoder = new();
    private readonly float[] positiveOscillatorI;
    private readonly float[] positiveOscillatorQ;
    private readonly float[] negativeOscillatorI;
    private readonly float[] negativeOscillatorQ;
    private readonly byte[,] predecessors = new byte[TracebackDepth, 2];
    private readonly double[] confidenceHistory = new double[TracebackDepth];
    private readonly double[,] transitionCos = new double[2, 2];
    private readonly double[,] transitionSin = new double[2, 2];
    private int sampleCount;
    private double positiveI;
    private double positiveQ;
    private double negativeI;
    private double negativeQ;
    private long symbolIndex;
    private double positiveScore;
    private double negativeScore;
    private Correlation currentCenter;
    private Correlation previousCenter;
    private bool hasPreviousCorrelations;
    private double recentConfidence;

    public CoherentGmskSequenceDecoder(int offset, int frequencyOffsetHz)
    {
        this.offset = offset;
        this.frequencyOffsetHz = frequencyOffsetHz;
        positiveOscillatorI = new float[AisReceiver.SamplesPerSymbol];
        positiveOscillatorQ = new float[AisReceiver.SamplesPerSymbol];
        negativeOscillatorI = new float[AisReceiver.SamplesPerSymbol];
        negativeOscillatorQ = new float[AisReceiver.SamplesPerSymbol];
        CreateOscillator(2_400 + frequencyOffsetHz, positiveOscillatorI, positiveOscillatorQ);
        CreateOscillator(-2_400 + frequencyOffsetHz, negativeOscillatorI, negativeOscillatorQ);
        for (int previousState = 0; previousState < 2; previousState++)
        for (int currentState = 0; currentState < 2; currentState++)
        {
            double previousSign = previousState == 1 ? 1 : -1;
            double currentSign = currentState == 1 ? 1 : -1;
            double expected = (previousSign + currentSign) * ToneRadiansPerSymbol * 0.5 +
                2 * Math.PI * frequencyOffsetHz / AisReceiver.SymbolRate;
            transitionCos[previousState, currentState] = Math.Cos(expected);
            transitionSin[previousState, currentState] = Math.Sin(expected);
        }
    }

    public int FrequencyOffsetHz => frequencyOffsetHz;
    public long RejectedFrames => decoder.RejectedFrames;
    public long FlagCount => decoder.FlagCount;
    public long FrameCandidateCount => decoder.FrameCandidateCount;
    public long ValidFrames => decoder.ValidFrames;

    public void Reset()
    {
        decoder.Reset();
        sampleCount = 0;
        positiveI = positiveQ = negativeI = negativeQ = 0;
        symbolIndex = 0;
        positiveScore = negativeScore = 0;
        currentCenter = previousCenter = default;
        hasPreviousCorrelations = false;
        recentConfidence = 0;
        Array.Clear(predecessors);
        Array.Clear(confidenceHistory);
    }

    public byte[]? Feed(Complex32 sample, long sampleIndex, out double quality)
    {
        quality = 0;
        if (sampleIndex < offset) return null;

        Accumulate(sample, sampleCount);
        if (++sampleCount < AisReceiver.SamplesPerSymbol) return null;
        sampleCount = 0;

        var positive = new Correlation(positiveI, positiveQ);
        var negative = new Correlation(negativeI, negativeQ);
        Correlation center = currentCenter;
        positiveI = positiveQ = negativeI = negativeQ = 0;
        currentCenter = default;

        double totalPower = positive.Power + negative.Power + 1e-20;
        double confidence = Math.Abs(positive.Power - negative.Power) / totalPower;
        confidenceHistory[symbolIndex % TracebackDepth] = confidence;
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
        double positiveFromPositive = positiveScore + PhaseScore(observedCos, observedSin, 1, 1);
        double positiveFromNegative = negativeScore + PhaseScore(observedCos, observedSin, 0, 1);
        double negativeFromPositive = positiveScore + PhaseScore(observedCos, observedSin, 1, 0);
        double negativeFromNegative = negativeScore + PhaseScore(observedCos, observedSin, 0, 0);
        int slot = (int)(symbolIndex % TracebackDepth);
        predecessors[slot, 1] = positiveFromNegative > positiveFromPositive ? (byte)0 : (byte)1;
        predecessors[slot, 0] = negativeFromNegative > negativeFromPositive ? (byte)0 : (byte)1;
        positiveScore = Math.Max(positiveFromPositive, positiveFromNegative) + positiveEmission;
        negativeScore = Math.Max(negativeFromPositive, negativeFromNegative) + negativeEmission;
        double normalization = Math.Max(positiveScore, negativeScore);
        positiveScore -= normalization;
        negativeScore -= normalization;
        previousCenter = center;

        byte[]? payload = null;
        if (symbolIndex >= TracebackDepth)
        {
            int state = positiveScore >= negativeScore ? 1 : 0;
            for (int depth = 0; depth < TracebackDepth; depth++)
            {
                int historySlot = (int)((symbolIndex - depth) % TracebackDepth);
                state = predecessors[historySlot, state];
            }
            int decidedSlot = (int)((symbolIndex - TracebackDepth) % TracebackDepth);
            recentConfidence = recentConfidence == 0
                ? confidenceHistory[decidedSlot]
                : recentConfidence * 0.94 + confidenceHistory[decidedSlot] * 0.06;
            quality = Math.Clamp(recentConfidence, 0, 1);
            payload = decoder.FeedLevel(state != 0);
        }
        symbolIndex++;
        return payload;
    }

    private void Accumulate(Complex32 sample, int index)
    {
        // x * conjugate(local oscillator)
        positiveI += sample.I * positiveOscillatorI[index] + sample.Q * positiveOscillatorQ[index];
        positiveQ += sample.Q * positiveOscillatorI[index] - sample.I * positiveOscillatorQ[index];
        negativeI += sample.I * negativeOscillatorI[index] + sample.Q * negativeOscillatorQ[index];
        negativeQ += sample.Q * negativeOscillatorI[index] - sample.I * negativeOscillatorQ[index];
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

    private double PhaseScore(double observedCos, double observedSin, int previous, int current) =>
        1.15 * Math.Clamp(
            observedCos * transitionCos[previous, current] +
            observedSin * transitionSin[previous, current], -1, 1);

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

    private readonly record struct Correlation(double I, double Q)
    {
        public double Power => I * I + Q * Q;
    }
}
