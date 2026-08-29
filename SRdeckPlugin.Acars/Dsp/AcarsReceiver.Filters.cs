using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Acars.Dsp;

/// <summary>Streaming VHF ACARS AM-MSK receiver.</summary>
public sealed partial class AcarsReceiver
{
    private static int Pack(ReadOnlySpan<bool> bits, int offset, bool invert,
        Span<byte> destination)
    {
        int byteCount = (bits.Length - offset) / 8;
        if (destination.Length < byteCount)
            throw new ArgumentException("The ACARS packed-byte buffer is too small.",
                nameof(destination));
        Span<byte> bytes = destination[..byteCount];
        bytes.Clear();
        for (int index = 0; index < byteCount; index++)
            for (int bit = 0; bit < 8; bit++)
                if (bits[offset + index * 8 + bit] ^ invert) bytes[index] |= (byte)(1 << bit);
        return byteCount;
    }

    /// <summary>
    /// Slow channel AGC applied after channel filtering and before AM envelope
    /// detection. Its time constants are far longer than an ACARS tone cycle,
    /// so it normalizes carrier level without suppressing the AM modulation.
    /// </summary>
    internal sealed class ChannelAgc
    {
        private const float TargetRms = 0.25f;
        private const float MinimumGain = 0.1f;
        private const float MaximumGain = 32f;
        private const float PeakLimit = 0.95f;
        private static readonly float PowerAttack = TimeConstant(0.025);
        private static readonly float PowerRelease = TimeConstant(0.5);
        private static readonly float GainAttack = TimeConstant(0.005);
        private static readonly float GainRelease = TimeConstant(0.1);
        private float estimatedPower;
        private float gain;

        public float CurrentGain => gain;
        public float EstimatedRms => MathF.Sqrt(MathF.Max(estimatedPower, 0));

        public void Reset()
        {
            estimatedPower = 0;
            gain = 1;
        }

        public (float I, float Q) Process(float inputI, float inputQ)
        {
            float power = inputI * inputI + inputQ * inputQ;
            if (!float.IsFinite(power))
            {
                Reset();
                return (0, 0);
            }
            float powerCoefficient = power > estimatedPower ? PowerAttack : PowerRelease;
            estimatedPower += powerCoefficient * (power - estimatedPower);
            float desiredGain = TargetRms / MathF.Sqrt(MathF.Max(estimatedPower, 1e-12f));
            desiredGain = Math.Clamp(desiredGain, MinimumGain, MaximumGain);
            float gainCoefficient = desiredGain < gain ? GainAttack : GainRelease;
            gain += gainCoefficient * (desiredGain - gain);
            if (!float.IsFinite(gain)) gain = 1;

            // Do not let a sudden carrier clip while the slow control loop is
            // still reducing gain. This limiter is inactive in normal AGC use.
            float magnitude = MathF.Sqrt(power);
            float appliedGain = magnitude > 0 ? MathF.Min(gain, PeakLimit / magnitude) : gain;
            return (inputI * appliedGain, inputQ * appliedGain);
        }

        private static float TimeConstant(double seconds) =>
            (float)(1 - Math.Exp(-1 / (seconds * DemodulationSampleRateHz)));
    }

    /// <summary>
    /// Delays monitor audio slightly so the fast detector can open before the
    /// beginning of an ACARS burst reaches the output. The decoder continues to
    /// use the undelayed, ungated audio buffer.
    /// </summary>
    internal sealed class MonitorAudioSquelchGate
    {
        private const int AttackSamples = DemodulationSampleRateHz * 3 / 1_000;
        private const int ReleaseSamples = DemodulationSampleRateHz * 5 / 1_000;
        private readonly float[] delay = new float[MonitorAudioDelaySamples];
        private int position;
        private float gain;

        public float Process(float sample, bool isOpen)
        {
            float delayed = delay[position];
            delay[position] = sample;
            if (++position == delay.Length) position = 0;

            if (isOpen)
                gain = MathF.Min(1f, gain + 1f / AttackSamples);
            else
                gain = MathF.Max(0f, gain - 1f / ReleaseSamples);
            return delayed * gain;
        }

        public void Reset()
        {
            Array.Clear(delay);
            position = 0;
            gain = 0;
        }
    }

    /// <summary>
    /// Selects the ACARS RF channel after rate conversion and before AM
    /// envelope detection. This is intentionally separate from the
    /// post-detection audio filter below: an envelope detector cannot remove
    /// an adjacent RF carrier after the nonlinear operation has happened.
    /// </summary>
    internal sealed class IqChannelLowPass
    {
        private readonly Biquad firstI = new(0.5411961f);
        private readonly Biquad firstQ = new(0.5411961f);
        private readonly Biquad secondI = new(1.306563f);
        private readonly Biquad secondQ = new(1.306563f);

        public void Process(float inputI, float inputQ, out float outputI, out float outputQ)
        {
            outputI = secondI.Process(firstI.Process(inputI));
            outputQ = secondQ.Process(firstQ.Process(inputQ));
        }

        public void Reset()
        {
            firstI.Reset();
            firstQ.Reset();
            secondI.Reset();
            secondQ.Reset();
        }

        private sealed class Biquad
        {
            private readonly float b0;
            private readonly float b1;
            private readonly float b2;
            private readonly float a1;
            private readonly float a2;
            private float z1;
            private float z2;

            public Biquad(float q)
            {
                float omega = 2f * MathF.PI * DemodulationIqCutoffHz /
                    DemodulationSampleRateHz;
                float cosine = MathF.Cos(omega);
                float alpha = MathF.Sin(omega) / (2f * q);
                float inverseA0 = 1f / (1f + alpha);
                b0 = (1f - cosine) * 0.5f * inverseA0;
                b1 = (1f - cosine) * inverseA0;
                b2 = b0;
                a1 = -2f * cosine * inverseA0;
                a2 = (1f - alpha) * inverseA0;
            }

            public float Process(float input)
            {
                float output = b0 * input + z1;
                z1 = b1 * input - a1 * output + z2;
                z2 = b2 * input - a2 * output;
                return output;
            }

            public void Reset() => z1 = z2 = 0;
        }
    }

    /// <summary>
    /// Fourth-order low-pass for the detected AM waveform. ACARS only uses
    /// 1,200 and 2,400 Hz tones, so rejecting detector noise above 4 kHz
    /// improves the correlator input without running a costly filter at the
    /// multi-MS/s host rate.
    /// </summary>
    internal sealed class DemodulatedAudioLowPass
    {
        private readonly Biquad first = new(0.5411961f);
        private readonly Biquad second = new(1.306563f);

        public float Process(float input) => second.Process(first.Process(input));

        public void Reset()
        {
            first.Reset();
            second.Reset();
        }

        private sealed class Biquad
        {
            private readonly float b0;
            private readonly float b1;
            private readonly float b2;
            private readonly float a1;
            private readonly float a2;
            private float z1;
            private float z2;

            public Biquad(float q)
            {
                const float cutoffHz = 4_000f;
                float omega = 2f * MathF.PI * cutoffHz / DemodulationSampleRateHz;
                float cosine = MathF.Cos(omega);
                float alpha = MathF.Sin(omega) / (2f * q);
                float inverseA0 = 1f / (1f + alpha);
                b0 = (1f - cosine) * 0.5f * inverseA0;
                b1 = (1f - cosine) * inverseA0;
                b2 = b0;
                a1 = -2f * cosine * inverseA0;
                a2 = (1f - alpha) * inverseA0;
            }

            public float Process(float input)
            {
                float output = b0 * input + z1;
                z1 = b1 * input - a1 * output + z2;
                z2 = b2 * input - a2 * output;
                return output;
            }

            public void Reset() => z1 = z2 = 0;
        }
    }
}
