using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Aprs.Dsp;

/// <summary>FM discriminator, Bell 202 or G3RUH 9600 demodulation and bit clock recovery.</summary>
public sealed class AprsReceiver
{
    public const int SampleRateHz = 48_000;
    public const int BandwidthHz = 15_000;
    public const int GmskBandwidthHz = 20_000;
    private const int SamplesPerBit = 40;
    private static readonly double[] MarkCos = Wave(1200, false), MarkSin = Wave(1200, true);
    private static readonly double[] SpaceCos = Wave(2200, false), SpaceSin = Wave(2200, true);
    private readonly double[,] products = new double[4, SamplesPerBit];
    private readonly double[] sums = new double[4];
    private readonly double[] baseband = new double[5];
    private Complex32 previous;
    private bool hasPrevious, hasTone, lastTone, lastSymbol;
    private int oscillator, ring;
    private int scrambler;
    private double dc, clock, basebandSum;
    public AprsModulation Modulation { get; private set; }
    public HdlcDecoder Hdlc { get; } = new();
    public long SamplesProcessed { get; private set; }
    public float SignalPowerDbfs { get; private set; } = -150;
    public event Action<byte[]>? FrameDecoded { add => Hdlc.FrameDecoded += value; remove => Hdlc.FrameDecoded -= value; }

    public AprsReceiver(AprsModulation modulation = AprsModulation.Afsk1200) => Reset(modulation);

    public static int GetBandwidthHz(AprsModulation modulation) =>
        modulation == AprsModulation.Gmsk9600 ? GmskBandwidthHz : BandwidthHz;

    public void Reset(AprsModulation? modulation = null)
    {
        if (modulation is { } value)
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(modulation));
            Modulation = value;
        }
        Array.Clear(products); Array.Clear(sums); Array.Clear(baseband);
        previous = default;
        hasPrevious = hasTone = lastTone = lastSymbol = false;
        oscillator = ring = scrambler = 0; dc = clock = basebandSum = 0;
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
        if (Modulation == AprsModulation.Gmsk9600)
        {
            basebandSum += audio - baseband[ring];
            baseband[ring] = audio;
            ring = (ring + 1) % baseband.Length;
            PushSymbol(basebandSum >= 0, baseband.Length);
            return;
        }
        Update(0, audio * MarkCos[oscillator]); Update(1, audio * MarkSin[oscillator]);
        Update(2, audio * SpaceCos[oscillator]); Update(3, audio * SpaceSin[oscillator]);
        oscillator = (oscillator + 1) % 240;
        ring = (ring + 1) % SamplesPerBit;
        bool tone = sums[0] * sums[0] + sums[1] * sums[1] >= sums[2] * sums[2] + sums[3] * sums[3];
        PushSymbol(tone, SamplesPerBit);
    }

    private void PushSymbol(bool tone, int samplesPerBit)
    {
        if (hasTone && tone != lastTone)
        {
            // Filtered transitions mark delayed symbol boundaries; steer gradually against noise.
            double error = clock < .5 ? clock : clock - 1;
            clock -= .2 * error;
            if (clock < 0) clock += 1;
            if (clock >= 1) clock -= 1;
        }
        double before = clock;
        clock += 1.0 / samplesPerBit;
        if (before < .5 && clock >= .5)
        {
            bool symbol = tone;
            if (Modulation == AprsModulation.Gmsk9600)
            {
                // G3RUH: undo 1 + x^12 + x^17 before NRZI decoding.
                symbol ^= (((scrambler >> 11) ^ (scrambler >> 16)) & 1) != 0;
                scrambler = ((scrambler << 1) | (tone ? 1 : 0)) & 0x1ffff;
            }
            if (hasTone) Hdlc.PushBit(symbol == lastSymbol); // NRZI: zero changes level.
            lastSymbol = symbol;
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
