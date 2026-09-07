using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.MaritimeVhf.Dsp;

internal sealed class NarrowFmReceiver
{
    public const int SampleRateHz = 48_000;
    private const float DcPole = 0.995f;
    private const float VoiceHighPassPole = 0.975f;
    private static readonly float AudioLowPassAlpha =
        1f - MathF.Exp(-2f * MathF.PI * 3_200f / SampleRateHz);
    private static readonly float DeemphasisAlpha =
        1f - MathF.Exp(-1f / (0.000_750f * SampleRateHz));
    private Complex32 previousIq;
    private bool hasPreviousIq;
    private float dcPreviousInput;
    private float dcPreviousOutput;
    private float lowPass;
    private float deemphasis;
    private float voicePreviousInput;
    private float voicePreviousOutput;
    private int squelchHoldSamples;

    public float SignalLevelDbfs { get; private set; } = -140f;
    public bool IsSquelchOpen { get; private set; }

    public int Process(
        ReadOnlySpan<Complex32> input,
        Span<float> output,
        bool voiceAudio,
        bool squelchEnabled,
        float squelchThresholdDbfs)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The audio output buffer is too small.", nameof(output));
        if (input.IsEmpty) return 0;

        double power = 0;
        for (int index = 0; index < input.Length; index++)
        {
            Complex32 current = input[index];
            power += current.I * current.I + current.Q * current.Q;
            float demodulated = 0;
            if (hasPreviousIq)
            {
                float dot = previousIq.I * current.I + previousIq.Q * current.Q;
                float cross = previousIq.I * current.Q - previousIq.Q * current.I;
                demodulated = MathF.Atan2(cross, dot);
            }
            previousIq = current;
            hasPreviousIq = true;

            float dcBlocked = demodulated - dcPreviousInput + DcPole * dcPreviousOutput;
            dcPreviousInput = demodulated;
            dcPreviousOutput = dcBlocked;
            lowPass += AudioLowPassAlpha * (dcBlocked - lowPass);
            if (!voiceAudio)
            {
                output[index] = lowPass;
                continue;
            }

            deemphasis += DeemphasisAlpha * (lowPass - deemphasis);
            float highPassed = deemphasis - voicePreviousInput +
                VoiceHighPassPole * voicePreviousOutput;
            voicePreviousInput = deemphasis;
            voicePreviousOutput = highPassed;
            output[index] = Math.Clamp(highPassed * 4f, -1f, 1f);
        }

        float measured = (float)(10 * Math.Log10(Math.Max(power / input.Length, 1e-14)));
        SignalLevelDbfs = SignalLevelDbfs <= -139f
            ? measured
            : SignalLevelDbfs * 0.82f + measured * 0.18f;
        UpdateSquelch(input.Length, squelchEnabled, squelchThresholdDbfs);
        if (voiceAudio && !IsSquelchOpen) output[..input.Length].Clear();
        return input.Length;
    }

    public void Reset()
    {
        previousIq = default;
        hasPreviousIq = false;
        dcPreviousInput = 0;
        dcPreviousOutput = 0;
        lowPass = 0;
        deemphasis = 0;
        voicePreviousInput = 0;
        voicePreviousOutput = 0;
        squelchHoldSamples = 0;
        SignalLevelDbfs = -140f;
        IsSquelchOpen = false;
    }

    private void UpdateSquelch(int sampleCount, bool enabled, float threshold)
    {
        if (!enabled)
        {
            IsSquelchOpen = true;
            squelchHoldSamples = SampleRateHz / 4;
            return;
        }
        float effectiveThreshold = IsSquelchOpen ? threshold - 3f : threshold;
        if (SignalLevelDbfs >= effectiveThreshold)
        {
            IsSquelchOpen = true;
            squelchHoldSamples = SampleRateHz / 3;
        }
        else if (squelchHoldSamples > 0)
        {
            squelchHoldSamples = Math.Max(0, squelchHoldSamples - sampleCount);
        }
        else
        {
            IsSquelchOpen = false;
        }
    }
}
