using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sstv.Dsp;

internal sealed class SstvDemodulator
{
    private const int Taps = 97;
    private static readonly double[] Filter = CreateFilter();
    private readonly double[] real = new double[Taps];
    private readonly double[] imaginary = new double[Taps];
    private int cursor;
    private Complex32 previousIq;
    private double previousReal;
    private double previousImaginary;
    private long sampleIndex;
    private bool hasPreviousIq;

    public void Reset()
    {
        Array.Clear(real);
        Array.Clear(imaginary);
        cursor = 0;
        sampleIndex = 0;
        previousIq = default;
        previousReal = previousImaginary = 0;
        hasPreviousIq = false;
    }

    public double Process(Complex32 sample, bool fm)
    {
        double power = (double)sample.I * sample.I + (double)sample.Q * sample.Q;
        if (!double.IsFinite(power) || power < 1e-18)
        {
            hasPreviousIq = false;
            return 0;
        }
        double phase = hasPreviousIq ? Math.Atan2(
            sample.Q * (double)previousIq.I - sample.I * (double)previousIq.Q,
            sample.I * (double)previousIq.I + sample.Q * (double)previousIq.Q) : 0;
        previousIq = sample;
        bool valid = hasPreviousIq;
        hasPreviousIq = true;
        if (!fm) return valid ? SstvReceiver.AudioCenterHz +
            phase * SstvReceiver.SampleRateHz / (2 * Math.PI) : 0;

        // Translate the real FM audio to an analytic baseband before measuring its tone.
        double angle = 2 * Math.PI * SstvReceiver.AudioCenterHz *
            (sampleIndex++ % SstvReceiver.SampleRateHz) / SstvReceiver.SampleRateHz;
        real[cursor] = phase * Math.Cos(angle);
        imaginary[cursor] = -phase * Math.Sin(angle);
        double re = 0, im = 0;
        for (int tap = 0, index = cursor; tap < Taps; tap++)
        {
            re += Filter[tap] * real[index];
            im += Filter[tap] * imaginary[index];
            if (--index < 0) index = Taps - 1;
        }
        cursor = (cursor + 1) % Taps;
        double frequency = SstvReceiver.AudioCenterHz + Math.Atan2(
            im * previousReal - re * previousImaginary,
            re * previousReal + im * previousImaginary) * SstvReceiver.SampleRateHz / (2 * Math.PI);
        double analyticPower = re * re + im * im;
        previousReal = re;
        previousImaginary = im;
        return analyticPower > 1e-14 && valid ? frequency : 0;
    }

    private static double[] CreateFilter()
    {
        var filter = new double[Taps];
        double sum = 0;
        double cutoff = 1_200.0 / SstvReceiver.SampleRateHz;
        for (int i = 0; i < Taps; i++)
        {
            int n = i - Taps / 2;
            filter[i] = (n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n)) *
                (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) +
                 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1)));
            sum += filter[i];
        }
        for (int i = 0; i < Taps; i++) filter[i] /= sum;
        return filter;
    }
}
