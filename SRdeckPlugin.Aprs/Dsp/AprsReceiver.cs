using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Aprs.Dsp;

/// <summary>NFM discriminator, Bell 202 noncoherent tone correlators and bit clock recovery.</summary>
public sealed class AprsReceiver
{
    public const int SampleRateHz = 48_000;
    public const int BandwidthHz = 15_000;
    private const int SamplesPerBit = 40;
    private static readonly double[] MarkCos = Wave(1200, false), MarkSin = Wave(1200, true);
    private static readonly double[] SpaceCos = Wave(2200, false), SpaceSin = Wave(2200, true);
    private readonly double[,] products = new double[4, SamplesPerBit];
    private readonly double[] sums = new double[4];
    private Complex32 previous;
    private bool hasPrevious, hasTone, lastTone, lastSymbol;
    private int oscillator, ring;
    private double dc, clock;
    public HdlcDecoder Hdlc { get; } = new();
    public long SamplesProcessed { get; private set; }
    public float SignalPowerDbfs { get; private set; } = -150;
    public event Action<byte[]>? FrameDecoded { add => Hdlc.FrameDecoded += value; remove => Hdlc.FrameDecoded -= value; }

    public void Reset()
    {
        Array.Clear(products); Array.Clear(sums);
        previous = default;
        hasPrevious = hasTone = lastTone = lastSymbol = false;
        oscillator = ring = 0; dc = clock = 0;
        SamplesProcessed = 0; SignalPowerDbfs = -150;
        Hdlc.Reset();
    }

    public void Process(ReadOnlySpan<Complex32> samples)
    {
        double power = 0;
        foreach (Complex32 sample in samples)
        {
            double p = (double)sample.I * sample.I + (double)sample.Q * sample.Q;
            if (!double.IsFinite(p)) { Reset(); continue; }
            power += p;
            if (hasPrevious)
            {
                double real = (double)sample.I * previous.I + (double)sample.Q * previous.Q;
                double imag = (double)sample.Q * previous.I - (double)sample.I * previous.Q;
                PushAudio(Math.Atan2(imag, real));
            }
            previous = sample; hasPrevious = true; SamplesProcessed++;
        }
        if (samples.Length > 0) SignalPowerDbfs = (float)(10 * Math.Log10(Math.Max(1e-15, power / samples.Length)));
    }

    private void PushAudio(double audio)
    {
        dc += .001 * (audio - dc);
        audio -= dc;
        Update(0, audio * MarkCos[oscillator]); Update(1, audio * MarkSin[oscillator]);
        Update(2, audio * SpaceCos[oscillator]); Update(3, audio * SpaceSin[oscillator]);
        oscillator = (oscillator + 1) % 240;
        ring = (ring + 1) % SamplesPerBit;
        bool tone = sums[0] * sums[0] + sums[1] * sums[1] >= sums[2] * sums[2] + sums[3] * sums[3];
        if (hasTone && tone != lastTone)
        {
            // Tone transitions define symbol boundaries, delayed by the correlation
            // window. Steer gradually so isolated noise cannot reset the bit clock.
            double error = clock < .5 ? clock : clock - 1;
            clock -= .2 * error;
            if (clock < 0) clock += 1;
            if (clock >= 1) clock -= 1;
        }
        double before = clock;
        clock += 1.0 / SamplesPerBit;
        if (before < .5 && clock >= .5)
        {
            if (hasTone) Hdlc.PushBit(tone == lastSymbol); // NRZI: zero changes tone.
            lastSymbol = tone;
        }
        if (clock >= 1) clock -= 1;
        lastTone = tone; hasTone = true;
    }

    private void Update(int index, double value)
    {
        sums[index] += value - products[index, ring];
        products[index, ring] = value;
    }

    private static double[] Wave(int frequency, bool sine) => Enumerable.Range(0, 240)
        .Select(n => sine ? Math.Sin(2 * Math.PI * frequency * n / SampleRateHz) : Math.Cos(2 * Math.PI * frequency * n / SampleRateHz)).ToArray();
}
