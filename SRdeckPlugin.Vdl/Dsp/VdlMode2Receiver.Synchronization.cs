using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    private void StartTimingRecovery(double firstSymbolTime, Complex32 firstSymbol)
    {
        timingCenterTime = firstSymbolTime;
        timingCenterSample = firstSymbol;
        timingErrorPending = true;
        timingRateCorrection = 0;
        lastTimingError = 0;
        lastTimingOffsetSamples = 0;
        nextSymbolTime = firstSymbolTime + SamplesPerSymbol + InitialTimingOffsetSamples;
    }

    private void UpdateTimingRecovery()
    {
        if (!timingErrorPending) return;
        if (!TimingRecoveryEnabled)
        {
            timingErrorPending = false;
            return;
        }
        double halfSymbol = SamplesPerSymbol * 0.5;
        double earlyTime = timingCenterTime - halfSymbol;
        double lateTime = timingCenterTime + halfSymbol;
        if (!CanInterpolate(earlyTime) || !CanInterpolate(lateTime)) return;
        Complex32 early = Interpolate(earlyTime);
        Complex32 late = Interpolate(lateTime);
        double deltaI = late.I - early.I;
        double deltaQ = late.Q - early.Q;
        double numerator = deltaI * timingCenterSample.I + deltaQ * timingCenterSample.Q;
        double energy = early.I * early.I + early.Q * early.Q +
                        late.I * late.I + late.Q * late.Q +
                        2 * (timingCenterSample.I * timingCenterSample.I + timingCenterSample.Q * timingCenterSample.Q);
        double error = Math.Clamp(numerator / Math.Max(energy, 1e-12), -1, 1);
        double loopError = Math.Clamp(error, -0.35, 0.35) * TimingRecoveryLoopGainScale;
        double rateDelta = TimingIntegralGain * loopError;
        timingRateCorrection = Math.Clamp(timingRateCorrection + rateDelta,
            -MaximumTimingRateCorrection, MaximumTimingRateCorrection);
        nextSymbolTime += TimingProportionalGain * loopError + rateDelta;
        lastTimingError = error;
        timingUpdateCount++;
        burstTimingErrorPower += error * error;
        burstTimingUpdateCount++;
        timingErrorPending = false;
    }

    private bool CanInterpolate(double time)
    {
        long sample = (long)Math.Floor(time);
        return sample - 1 >= Math.Max(0, workingSampleIndex - TimingBufferLength + 1) &&
               sample + 2 <= workingSampleIndex;
    }

    private Complex32 Interpolate(double time)
    {
        long sample = (long)Math.Floor(time);
        double fraction = time - sample;
        Complex32 p0 = TimingSample(sample - 1);
        Complex32 p1 = TimingSample(sample);
        Complex32 p2 = TimingSample(sample + 1);
        Complex32 p3 = TimingSample(sample + 2);
        return new((float)Cubic(p0.I, p1.I, p2.I, p3.I, fraction),
            (float)Cubic(p0.Q, p1.Q, p2.Q, p3.Q, fraction));
    }

    private Complex32 TimingSample(long index) => timingBuffer[(int)(index % TimingBufferLength)];

    private static double Cubic(double p0, double p1, double p2, double p3, double fraction)
    {
        double a = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
        double b = p0 - 2.5 * p1 + 2 * p2 - 0.5 * p3;
        double c = -0.5 * p0 + 0.5 * p2;
        return ((a * fraction + b) * fraction + c) * fraction + p1;
    }

    private void AcceptSymbol(Complex32 sample, double symbolTime,
        IqBlockMetadata metadata, List<VdlFrame> output)
    {
        sample = ApplyAdaptiveEqualizer(sample);
        burstSymbols.Add(sample);
        burstSymbolsEarly.Add(CanInterpolate(symbolTime - 0.25)
            ? Interpolate(symbolTime - 0.25) : sample);
        burstSymbolsLate.Add(CanInterpolate(symbolTime + 0.25)
            ? Interpolate(symbolTime + 0.25) : sample);
        double phaseNow = Math.Atan2(sample.Q, sample.I);
        double difference = WrapPositive(phaseNow - previousPhase - phaseDriftPerSymbol);
        previousPhase = phaseNow;
        int phaseIndex = ((int)Math.Round(difference / (Math.PI / 4))) & 7;
        double decidedDifference = phaseIndex * Math.PI / 4;
        double carrierError = WrapSigned(difference - decidedDifference);
        lastCarrierError = carrierError;
        carrierErrorPower += carrierError * carrierError;
        carrierUpdateCount++;
        burstCarrierErrorPower += carrierError * carrierError;
        burstCarrierUpdateCount++;
        phaseDriftPerSymbol = Math.Clamp(
            phaseDriftPerSymbol + CarrierTrackingLoopGain * carrierError,
            -MaximumCarrierCorrection, MaximumCarrierCorrection);
        if (adaptiveEqualizerConfigured) adaptiveEqualizerCarrierPhase += phaseDriftPerSymbol;
        int decoded = GrayCode[phaseIndex];
        double reliability = Math.Clamp(1 - Math.Abs(carrierError) / (Math.PI / 8), 0, 1);
        burstBits.Add((decoded & 4) != 0);
        burstBits.Add((decoded & 2) != 0);
        burstBits.Add((decoded & 1) != 0);
        burstReliabilities.Add(reliability);
        burstReliabilities.Add(reliability);
        burstReliabilities.Add(reliability);

        if (requestedBurstBits == HeaderLength && burstBits.Count >= HeaderLength)
        {
            if (!TryReadHeader(burstBits, out transmissionLength, out int dataOctets, out int fecOctets,
                    out HeaderDecodeStatus headerStatus))
            {
                HeaderRejectedCount++;
                if (headerStatus == HeaderDecodeStatus.FecUncorrectable) HeaderFecRejectedCount++;
                else HeaderLengthRejectedCount++;
                ResetBurst();
                return;
            }
            HeaderAcceptedCount++;
            if (headerStatus == HeaderDecodeStatus.Corrected) HeaderCorrectedCount++;
            else HeaderCleanCount++;
            requestedBurstBits = HeaderLength + 8 * (dataOctets + fecOctets);
        }

        if (requestedBurstBits > HeaderLength && burstBits.Count >= requestedBurstBits)
        {
            DecodeBurst(metadata, output);
            ResetBurst();
        }
        else if (burstBits.Count > HeaderLength + 8 * 3_000)
        {
            BurstTimeoutCount++;
            ResetBurst();
        }
    }

    private double CalculateSynchronizationError(out double finalPhase, out double drift,
        out double coherence, out double preamblePower)
    {
        int startIndex = PreambleSymbols - PreambleVerificationSymbols;
        Span<double> errors = stackalloc double[PreambleVerificationSymbols];
        double unwrap = 0;
        double mean = 0;
        double previousError = 0;
        for (int i = 0; i < PreambleVerificationSymbols; i++)
        {
            int symbol = startIndex + i;
            int chronologicalIndex = symbol * SamplesPerSymbol;
            int ringIndex = (syncWriteIndex + chronologicalIndex) % syncBuffer.Length;
            Complex32 value = syncBuffer[ringIndex];
            double phase = Math.Atan2(value.Q, value.I);
            double error = phase - PreamblePhases[symbol] * Math.PI / 4;
            if (i > 0)
            {
                double delta = error - previousError;
                if (delta > Math.PI) unwrap -= 2 * Math.PI;
                else if (delta < -Math.PI) unwrap += 2 * Math.PI;
            }
            previousError = error;
            errors[i] = error + unwrap;
            mean += errors[i];
        }
        mean /= PreambleVerificationSymbols;

        double center = (PreambleVerificationSymbols - 1) / 2.0;
        double numerator = 0;
        double denominator = 0;
        for (int i = 0; i < PreambleVerificationSymbols; i++)
        {
            double x = i - center;
            numerator += x * (errors[i] - mean);
            denominator += x * x;
        }
        drift = numerator / denominator;
        double squaredError = 0;
        double amplitudeSum = 0;
        double amplitudeSquaredSum = 0;
        double correlationI = 0;
        double correlationQ = 0;
        double power = 0;
        for (int i = 0; i < PreambleVerificationSymbols; i++)
        {
            int symbol = startIndex + i;
            double residual = errors[i] - mean - drift * (i - center);
            squaredError += residual * residual;
            int chronologicalIndex = symbol * SamplesPerSymbol;
            int ringIndex = (syncWriteIndex + chronologicalIndex) % syncBuffer.Length;
            Complex32 value = syncBuffer[ringIndex];
            double amplitude = Math.Sqrt(value.I * value.I + value.Q * value.Q);
            amplitudeSum += amplitude;
            amplitudeSquaredSum += amplitude * amplitude;
            double referencePhase = PreamblePhases[symbol] * Math.PI / 4 + drift * (i - center);
            double cosine = Math.Cos(referencePhase);
            double sine = Math.Sin(referencePhase);
            correlationI += value.I * cosine + value.Q * sine;
            correlationQ += value.Q * cosine - value.I * sine;
            power += value.I * value.I + value.Q * value.Q;
        }
        coherence = power <= 1e-20 ? 0 :
            (correlationI * correlationI + correlationQ * correlationQ) / (PreambleVerificationSymbols * power);
        coherence = Math.Clamp(coherence, 0, 1);
        preamblePower = power / PreambleVerificationSymbols;
        lastPreamblePhaseResidualRms = Math.Sqrt(squaredError / PreambleVerificationSymbols);
        double amplitudeMean = amplitudeSum / PreambleVerificationSymbols;
        double amplitudeVariance = Math.Max(0, amplitudeSquaredSum / PreambleVerificationSymbols -
            amplitudeMean * amplitudeMean);
        lastPreambleAmplitudeCoefficientOfVariation = amplitudeMean <= 1e-12 ? 0 :
            Math.Sqrt(amplitudeVariance) / amplitudeMean;
        int lastIndex = (syncWriteIndex + syncBuffer.Length - SamplesPerSymbol) % syncBuffer.Length;
        Complex32 last = syncBuffer[lastIndex];
        finalPhase = Math.Atan2(last.Q, last.I);
        return squaredError;
    }

    private void UpdateNoiseEstimate(double power)
    {
        if (!double.IsFinite(power) || power < 0) return;
        if (noiseEstimateCount++ == 0)
        {
            noiseFloorPower = power;
            return;
        }
        double gain = power < noiseFloorPower ? 1.0 / 512 : 1.0 / 16_384;
        noiseFloorPower += gain * (power - noiseFloorPower);
    }

    private void RecordPreambleQuality(double coherence, double power)
    {
        lastPreambleCoherence = coherence;
        lastPreamblePower = power;
        lastPreambleSnrDb = noiseFloorPower <= 1e-20
            ? double.PositiveInfinity
            : 10 * Math.Log10(Math.Max(power, 1e-20) / noiseFloorPower);
    }

    private bool PreambleQualityAccepted() =>
        lastPreambleCoherence >= PreambleCoherenceThreshold &&
        (!double.IsFinite(lastPreambleSnrDb) || lastPreambleSnrDb >= PreambleSnrThresholdDb);

    private void CalculatePreambleChannelFit(double drift, double finalPhase,
        out double flatChannelNmse, out double threeTapChannelNmse, out Complex[] channelTaps)
    {
        int count = PreambleVerificationSymbols;
        int firstSymbol = PreambleSymbols - count;
        double center = (count - 1) / 2.0;
        // The accepted peak belongs to the preceding correlation window; the current
        // sample has already advanced the ring buffer by one position.
        int previousWindowStart = (syncWriteIndex + syncBuffer.Length - 1) % syncBuffer.Length;
        double phaseIntercept = finalPhase - PreamblePhases[^1] * Math.PI / 4 -
            drift * (count - 1 - center);
        var received = new Complex[count];
        var expected = new Complex[count];
        double receivedEnergy = 0;
        for (int index = 0; index < count; index++)
            expected[index] = Complex.FromPolarCoordinates(1,
                PreamblePhases[firstSymbol + index] * Math.PI / 4);

        // The first sample of the preceding window was overwritten by the current
        // input sample before we accepted its correlation peak. The remaining 15
        // preamble centres are intact and are sufficient for the channel fit.
        for (int index = 1; index < count; index++)
        {
            int ringIndex = (previousWindowStart + index * SamplesPerSymbol) % syncBuffer.Length;
            Complex32 sample = syncBuffer[ringIndex];
            double carrierPhase = phaseIntercept + drift * (index - center);
            Complex normalized = new Complex(sample.I, sample.Q) *
                Complex.FromPolarCoordinates(1, -carrierPhase);
            received[index] = normalized;
            receivedEnergy += normalized.Magnitude * normalized.Magnitude;
        }

        Complex flat = Complex.Zero;
        for (int index = 1; index < count; index++) flat += Complex.Conjugate(expected[index]) * received[index];
        flat /= count - 1;
        double flatError = 0;
        for (int index = 1; index < count; index++)
        {
            Complex error = received[index] - flat * expected[index];
            flatError += error.Magnitude * error.Magnitude;
        }
        flatChannelNmse = receivedEnergy <= 1e-20 ? 1 : flatError / receivedEnergy;

        var normal = new Complex[3, 3];
        var cross = new Complex[3];
        for (int index = 1; index < count; index++)
        {
            for (int row = 0; row < 3; row++)
            {
                Complex xRow = index >= row ? expected[index - row] : Complex.Zero;
                cross[row] += Complex.Conjugate(xRow) * received[index];
                for (int column = 0; column < 3; column++)
                {
                    Complex xColumn = index >= column ? expected[index - column] : Complex.Zero;
                    normal[row, column] += Complex.Conjugate(xRow) * xColumn;
                }
            }
        }
        if (!TrySolveComplex3x3(normal, cross, out Complex[] taps))
        {
            threeTapChannelNmse = flatChannelNmse;
            channelTaps = [];
            return;
        }
        double equalizedError = 0;
        for (int index = 1; index < count; index++)
        {
            Complex predicted = Complex.Zero;
            for (int tap = 0; tap < 3; tap++)
                if (index >= tap) predicted += taps[tap] * expected[index - tap];
            Complex error = received[index] - predicted;
            equalizedError += error.Magnitude * error.Magnitude;
        }
        threeTapChannelNmse = receivedEnergy <= 1e-20 ? 1 : equalizedError / receivedEnergy;
        channelTaps = taps;
    }

    private void ConfigureAdaptiveEqualizer(IReadOnlyList<Complex> channelTaps,
        double flatChannelNmse, double threeTapChannelNmse, double initialCarrierPhase)
    {
        adaptiveEqualizerConfigured = AdaptiveEqualizerEnabled && channelTaps.Count == 3 &&
            channelTaps[0].Magnitude > 1e-6 && flatChannelNmse > 0.02 &&
            threeTapChannelNmse < flatChannelNmse * 0.8;
        if (!adaptiveEqualizerConfigured) return;
        AdaptiveEqualizerAppliedCount++;
        adaptiveEqualizerTap0 = channelTaps[0];
        adaptiveEqualizerTap1 = channelTaps[1];
        adaptiveEqualizerTap2 = channelTaps[2];
        adaptiveEqualizerPrevious1 = Complex.FromPolarCoordinates(1, PreamblePhases[^1] * Math.PI / 4);
        adaptiveEqualizerPrevious2 = Complex.FromPolarCoordinates(1, PreamblePhases[^2] * Math.PI / 4);
        adaptiveEqualizerCarrierPhase = initialCarrierPhase;
    }

    private Complex32 ApplyAdaptiveEqualizer(Complex32 sample)
    {
        if (!adaptiveEqualizerConfigured) return sample;
        Complex carrier = Complex.FromPolarCoordinates(1, -adaptiveEqualizerCarrierPhase);
        Complex received = new Complex(sample.I, sample.Q) * carrier;
        Complex equalized = (received - adaptiveEqualizerTap1 * adaptiveEqualizerPrevious1 -
            adaptiveEqualizerTap2 * adaptiveEqualizerPrevious2) / adaptiveEqualizerTap0;
        adaptiveEqualizerPrevious2 = adaptiveEqualizerPrevious1;
        adaptiveEqualizerPrevious1 = equalized;
        Complex restored = equalized * Complex.Conjugate(carrier);
        return new((float)restored.Real, (float)restored.Imaginary);
    }

    private static bool TrySolveComplex3x3(Complex[,] source, Complex[] right,
        out Complex[] solution)
    {
        var augmented = new Complex[3, 4];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++) augmented[row, column] = source[row, column];
        for (int row = 0; row < 3; row++) augmented[row, 3] = right[row];

        for (int pivot = 0; pivot < 3; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < 3; row++)
                if (augmented[row, pivot].Magnitude > augmented[best, pivot].Magnitude) best = row;
            if (augmented[best, pivot].Magnitude < 1e-12)
            {
                solution = [];
                return false;
            }
            if (best != pivot)
                for (int column = pivot; column < 4; column++)
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
            Complex divisor = augmented[pivot, pivot];
            for (int column = pivot; column < 4; column++) augmented[pivot, column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                Complex factor = augmented[row, pivot];
                for (int column = pivot; column < 4; column++)
                    augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        solution = [augmented[0, 3], augmented[1, 3], augmented[2, 3]];
        return true;
    }
}
