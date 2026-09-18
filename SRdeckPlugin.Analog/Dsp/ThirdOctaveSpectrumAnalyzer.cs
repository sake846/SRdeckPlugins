using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Analog.Dsp;

/// <summary>
/// Measures the demodulated PCM in nominal one-third-octave bands from
/// 63 Hz through 16 kHz. Band edges are +/- one sixth of an octave.
/// </summary>
internal sealed class ThirdOctaveSpectrumAnalyzer
{
    private const int FftSize = 4096;
    private const float FloorDb = -72f;
    private static readonly float[] NominalCenters =
    [
        63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
        1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
        10000, 12500, 16000
    ];

    private readonly Radix2Fft fft = new(FftSize);
    private readonly float[] window = new float[FftSize];
    private readonly float[] real = new float[FftSize];
    private readonly float[] imaginary = new float[FftSize];
    private readonly float[] pending = new float[FftSize];
    private float[] published = new float[NominalCenters.Length];
    private int pendingCount;
    private double windowSum;

    public ThirdOctaveSpectrumAnalyzer()
    {
        Array.Fill(published, FloorDb);
        for (int index = 0; index < FftSize; index++)
        {
            float value = 0.5f - (0.5f * MathF.Cos(2 * MathF.PI * index / (FftSize - 1)));
            window[index] = value;
            windowSum += value;
        }
    }

    public void Process(ReadOnlySpan<float> interleavedSamples, int channels, int sampleRateHz)
    {
        if (channels <= 0 || sampleRateHz <= 0) return;

        int frameCount = interleavedSamples.Length / channels;
        for (int frame = 0; frame < frameCount; frame++)
        {
            float mono = 0;
            int frameOffset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                mono += interleavedSamples[frameOffset + channel];
            pending[pendingCount++] = mono / channels;

            if (pendingCount != FftSize) continue;
            Analyze(sampleRateHz);
            pendingCount = 0;
        }
    }

    public float[] GetLevelsDb() => Volatile.Read(ref published);

    public void Reset()
    {
        pendingCount = 0;
        Array.Clear(pending);
        float[] reset = new float[NominalCenters.Length];
        Array.Fill(reset, FloorDb);
        Volatile.Write(ref published, reset);
    }

    private void Analyze(int sampleRateHz)
    {
        for (int index = 0; index < FftSize; index++)
        {
            real[index] = pending[index] * window[index];
            imaginary[index] = 0;
        }
        fft.Transform(real, imaginary);

        float[] previous = Volatile.Read(ref published);
        var next = new float[NominalCenters.Length];
        double binWidth = sampleRateHz / (double)FftSize;
        double normalization = 2.0 / (windowSum * windowSum);

        for (int band = 0; band < NominalCenters.Length; band++)
        {
            // Exact base-2 center frequencies keep every band one third octave apart.
            double exactCenter = 1000.0 * Math.Pow(2.0, (band - 12) / 3.0);
            double lowerHz = exactCenter * Math.Pow(2.0, -1.0 / 6.0);
            double upperHz = Math.Min(exactCenter * Math.Pow(2.0, 1.0 / 6.0), sampleRateHz / 2.0);
            int firstBin = Math.Max(1, (int)Math.Ceiling(lowerHz / binWidth));
            int lastBin = Math.Min((FftSize / 2) - 1, (int)Math.Floor(upperHz / binWidth));
            double power = 0;
            for (int bin = firstBin; bin <= lastBin; bin++)
                power += ((double)real[bin] * real[bin]) + ((double)imaginary[bin] * imaginary[bin]);

            float measuredDb = (float)Math.Clamp(
                10.0 * Math.Log10(Math.Max(power * normalization, 1e-12)), FloorDb, 0.0);
            float smoothing = measuredDb > previous[band] ? 0.68f : 0.18f;
            next[band] = previous[band] + (smoothing * (measuredDb - previous[band]));
        }

        Volatile.Write(ref published, next);
    }
}
