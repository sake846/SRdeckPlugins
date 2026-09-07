using SRdeck.DSP;

namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Refines a coarse FFT peak by evaluating only the two adjacent half-bin DFT
/// points. Integer-bin powers come from the existing FFT, so the interpolation
/// uses the same five spectral samples as a twice-zero-padded FFT without
/// executing that larger transform. Detection power ratios remain unchanged.
/// </summary>
internal sealed class LoRaFinePeakEstimator
{
    private readonly Complex[] samples;
    private readonly int symbolSamples;
    private bool prepared;
    private int lastBin = -1;
    private float lastPosition;

    public LoRaFinePeakEstimator(int symbolSamples)
    {
        if (symbolSamples < 2 || (symbolSamples & (symbolSamples - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(symbolSamples));
        this.symbolSamples = symbolSamples;
        samples = new Complex[symbolSamples];
    }

    public void Prepare(ReadOnlySpan<Complex> dechirped)
    {
        if (dechirped.Length != symbolSamples)
            throw new ArgumentException("Expected one dechirped symbol.", nameof(dechirped));
        // Preserve one symbol before the coarse FFT overwrites its input.
        // Reuse this buffer for the main peak and all soft-decision candidates.
        dechirped.CopyTo(samples);
        prepared = true;
        lastBin = -1;
    }

    public float Interpolate(ReadOnlySpan<float> coarsePower, int coarseBin)
    {
        if (!prepared) throw new InvalidOperationException("Prepare a dechirped symbol first.");
        if (coarsePower.Length != symbolSamples)
            throw new ArgumentException("Expected the matching unpadded FFT power spectrum.", nameof(coarsePower));
        if ((uint)coarseBin >= (uint)symbolSamples) throw new ArgumentOutOfRangeException(nameof(coarseBin));
        // The main peak is requested again as the first soft candidate.
        if (coarseBin == lastBin) return lastPosition;

        (float halfLeft, float halfRight) = CalculateHalfBinPowers(coarseBin);
        int mask = symbolSamples - 1;
        float center = coarsePower[coarseBin];
        float left, middle, right, offset;
        if (halfLeft > center && halfLeft >= halfRight)
        {
            offset = -0.5f;
            left = coarsePower[(coarseBin - 1) & mask];
            middle = halfLeft;
            right = center;
        }
        else if (halfRight > center)
        {
            offset = 0.5f;
            left = center;
            middle = halfRight;
            right = coarsePower[(coarseBin + 1) & mask];
        }
        else
        {
            offset = 0;
            left = halfLeft;
            middle = center;
            right = halfRight;
        }
        float denominator = left - 2 * middle + right;
        float delta = Math.Abs(denominator) > 1e-12f
            ? Math.Clamp(0.5f * (left - right) / denominator, -0.5f, 0.5f)
            : 0;
        float position = coarseBin + offset + delta * 0.5f;
        lastPosition = position < 0 ? position + symbolSamples : position >= symbolSamples ? position - symbolSamples : position;
        lastBin = coarseBin;
        return lastPosition;
    }

    private (float Left, float Right) CalculateHalfBinPowers(int coarseBin)
    {
        // The coarse FFT output is fftshifted. Evaluate the corresponding
        // unshifted frequencies k +/- 1/2 with two complex oscillators in one
        // pass. Double accumulation limits phase/sum error at larger SFs.
        double bin = coarseBin - symbolSamples * 0.5;
        double leftAngle = -2 * Math.PI * (bin - 0.5) / symbolSamples;
        double rightAngle = -2 * Math.PI * (bin + 0.5) / symbolSamples;
        double leftStepI = Math.Cos(leftAngle), leftStepQ = Math.Sin(leftAngle);
        double rightStepI = Math.Cos(rightAngle), rightStepQ = Math.Sin(rightAngle);
        double leftI = 1, leftQ = 0, rightI = 1, rightQ = 0;
        double sumLeftI = 0, sumLeftQ = 0, sumRightI = 0, sumRightQ = 0;
        for (int n = 0; n < samples.Length; n++)
        {
            Complex sample = samples[n];
            sumLeftI += sample.X * leftI - sample.Y * leftQ;
            sumLeftQ += sample.X * leftQ + sample.Y * leftI;
            sumRightI += sample.X * rightI - sample.Y * rightQ;
            sumRightQ += sample.X * rightQ + sample.Y * rightI;

            double nextLeftI = leftI * leftStepI - leftQ * leftStepQ;
            leftQ = leftI * leftStepQ + leftQ * leftStepI;
            leftI = nextLeftI;
            double nextRightI = rightI * rightStepI - rightQ * rightStepQ;
            rightQ = rightI * rightStepQ + rightQ * rightStepI;
            rightI = nextRightI;
        }
        return ((float)(sumLeftI * sumLeftI + sumLeftQ * sumLeftQ),
                (float)(sumRightI * sumRightI + sumRightQ * sumRightQ));
    }
}
