namespace SRdeckPlugin.AribT98.Dsp;

/// <summary>Part 3 section 3.4: receive H(f) / sinc(fT), alpha=0.2.</summary>
public sealed class AribT98ReceiveFilter
{
    private static readonly float[] coefficients = Design(false);
    private static readonly double[] reversedCoefficients = AribT98FirKernel.Reverse(coefficients);
    private readonly double[] delay = new double[coefficients.Length * 2];
    private int index;
    public const int DelaySamples = 160;

    public float Process(float sample)
    {
        int taps = coefficients.Length;
        delay[index] = delay[index + taps] = sample;
        double result = AribT98FirKernel.Dot(delay.AsSpan(index + 1, taps), reversedCoefficients);
        if (++index == taps) index = 0;
        return (float)result;
    }

    public void Reset() { Array.Clear(delay); index = 0; }

    // Frequency integration creates a linear-phase windowed FIR. The transmit
    // variant is also used by the offline regression modulator (no RF output).
    public static float[] Design(bool transmit)
    {
        const int taps = 321, bins = 4096;
        var result = new float[taps];
        double dc = 0;
        for (int i = 0; i < taps; i++)
        {
            int t = i - taps/2;
            double sum = 0.5;
            for (int k = 1; k <= 246; k++)
            {
                double f = (double)k / bins * AribT98Receiver.SampleRateHz;
                double normalized = f / AribT98Receiver.SymbolRate;
                if (normalized >= 0.6) break;
                double rootRaisedCosine = normalized < 0.4 ? 1 : Math.Cos(Math.PI * (normalized - 0.4) / 0.4);
                double sinc = Math.Sin(Math.PI*normalized) / (Math.PI*normalized);
                double response = rootRaisedCosine * (transmit ? sinc : 1 / sinc);
                sum += response * Math.Cos(2*Math.PI*k*t / bins);
            }
            double window = 0.54 - 0.46*Math.Cos(2*Math.PI*i / (taps-1));
            result[i] = (float)(2*sum / bins * window);
            dc += result[i];
        }
        for (int i = 0; i < taps; i++) result[i] /= (float)dc;
        return result;
    }
}
