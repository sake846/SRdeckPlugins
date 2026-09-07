using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Models;

namespace SRdeckPlugin.Cw.Dsp;

/// <summary>
/// Wide-band CW receiver. A short-time FFT discovers and tracks simultaneous
/// carriers; one adaptive Morse timing decoder is maintained per carrier.
/// </summary>
public sealed class CwSkimmerReceiver
{
    public const int ReceiverBandwidthHz = CwBandwidth.DefaultHz;
    public const int OutputSampleRateHz = CwBandwidth.DefaultHz * 6 / 5;
    public const int FftSize = 512;
    public const int HopSize = 128;
    private const int MaximumTrackAgeSeconds = 8;
    private const int TrackConfirmationFrames = 5;
    private const int UnconfirmedTrackAgeFrames = 3;
    private const int KeyStateConfirmationFrames = 2;
    private const double SentenceBoundarySilenceMilliseconds = 1_000;
    private const double MinimumCarrierSpacingHz = 70;
    private const double KeyReleaseHysteresisDb = 2;
    private const double WeakSignalIntegrationMarginDb = 10;
    private const double IntegratedPeakDecayDbPerSecond = 4;

    private float[] inputReal = new float[ReceiverFftSize];
    private float[] inputImaginary = new float[ReceiverFftSize];
    private float[] spectrumReal = new float[ReceiverFftSize];
    private float[] spectrumImaginary = new float[ReceiverFftSize];
    private Radix2Fft fft = new(ReceiverFftSize);
    private double[] power = new double[ReceiverFftSize];
    private double[] noiseScratch = new double[ReceiverFftSize];
    private double[] window = CreateWindow(ReceiverFftSize);
    private float[] fftWindow = CreateFloatWindow(ReceiverFftSize);
    private double[] carrierWindow = CreateWindow(ReceiverHopSize);
    private readonly List<Peak> peakScratch = new(128);
    private readonly List<Peak> selectedPeakScratch = new(32);
    private double windowEnergy;
    private double carrierWindowEnergy;
    private int receiverFftSize = ReceiverFftSize;
    private int receiverHopSize = ReceiverHopSize;
    private readonly List<SignalTrack> tracks = [];
    private int inputCount;
    private int sampleRateHz;
    private long frameSequence;
    private long nextTrackId;

    public int ActiveSignalCount => tracks.Count(track => track.Confirmed &&
        frameSequence - track.LastSeenFrame < Math.Max(1, sampleRateHz / receiverHopSize));
    public long ProcessedFrameCount => frameSequence;

    public event EventHandler<CwDecodedEventArgs>? TextDecoded;

    private const int ReceiverFftSize = 512;
    private const int ReceiverHopSize = 128;

    public static int GetFftSize(int receiverBandwidthHz) =>
        NormalizeReceiverBandwidth(receiverBandwidthHz) switch
        {
            20_000 => 1_024,
            50_000 => 4_096,
            _ => ReceiverFftSize
        };

    public static int GetHopSize(int receiverBandwidthHz) =>
        GetFftSize(receiverBandwidthHz) / 4;

    public CwSkimmerReceiver()
    {
        windowEnergy = window.Sum(value => value * value);
        carrierWindowEnergy = carrierWindow.Sum(value => value * value);
    }

    public void Process(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        double minimumSnrDb = 8,
        int maximumSignals = 24,
        int receiverBandwidthHz = ReceiverBandwidthHz,
        CwLanguage defaultLanguage = CwLanguage.Western)
    {
        receiverBandwidthHz = NormalizeReceiverBandwidth(receiverBandwidthHz);
        ConfigureBandwidth(receiverBandwidthHz);
        int rate = metadata.Configuration.OutputSampleRateHz;
        if (rate < receiverBandwidthHz)
            throw new ArgumentException(
                $"The CW skimmer channel must cover the {CwBandwidth.Format(receiverBandwidthHz)} passband.",
                nameof(metadata));
        if (sampleRateHz != 0 && sampleRateHz != rate) Reset();
        sampleRateHz = rate;
        minimumSnrDb = Math.Clamp(minimumSnrDb, 3, 30);
        maximumSignals = Math.Clamp(maximumSignals, 1, 32);

        for (int index = 0; index < samples.Length; index++)
        {
            inputReal[inputCount] = samples[index].I;
            inputImaginary[inputCount] = samples[index].Q;
            inputCount++;
            if (inputCount != receiverFftSize) continue;

            ProcessFrame(metadata, minimumSnrDb, maximumSignals, receiverBandwidthHz, defaultLanguage);
            Array.Copy(inputReal, receiverHopSize, inputReal, 0,
                receiverFftSize - receiverHopSize);
            Array.Copy(inputImaginary, receiverHopSize, inputImaginary, 0,
                receiverFftSize - receiverHopSize);
            inputCount = receiverFftSize - receiverHopSize;
        }
    }

    public void Reset()
    {
        inputReal.AsSpan().Clear();
        inputImaginary.AsSpan().Clear();
        spectrumReal.AsSpan().Clear();
        spectrumImaginary.AsSpan().Clear();
        power.AsSpan().Clear();
        tracks.Clear();
        inputCount = 0;
        frameSequence = 0;
        sampleRateHz = 0;
    }

    private void ProcessFrame(
        ChannelIqBlockMetadata metadata,
        double minimumSnrDb,
        int maximumSignals,
        int receiverBandwidthHz,
        CwLanguage defaultLanguage)
    {
        frameSequence++;
        ApplyFftWindow(inputReal, inputImaginary, fftWindow,
            spectrumReal, spectrumImaginary);
        fft.Transform(spectrumReal, spectrumImaginary);

        int passbandBins = 0;
        for (int bin = 0; bin < receiverFftSize; bin++)
        {
            double offset = BinToOffset(bin);
            double real = spectrumReal[bin];
            double imaginary = spectrumImaginary[bin];
            power[bin] = real * real + imaginary * imaginary + 1e-24;
            if (Math.Abs(offset) <= receiverBandwidthHz / 2.0)
                noiseScratch[passbandBins++] = power[bin];
        }
        double noisePower = SelectNoisePower(noiseScratch.AsSpan(0, passbandBins));
        noisePower = Math.Max(noisePower, 1e-24);

        foreach (SignalTrack track in tracks) track.Matched = false;
        List<Peak> peaks = FindPeaks(noisePower, minimumSnrDb + 1.5, receiverBandwidthHz);
        foreach (Peak peak in peaks)
        {
            SignalTrack? track = FindTrack(peak.OffsetHz);
            if (track is null)
            {
                if (tracks.Count >= maximumSignals) continue;
                track = new SignalTrack(++nextTrackId, peak.OffsetHz, frameSequence, defaultLanguage);
                tracks.Add(track);
            }
            track.OffsetHz = track.OffsetHz * 0.82 + peak.OffsetHz * 0.18;
            track.ConsecutiveMatches = frameSequence == track.LastSeenFrame + 1
                ? track.ConsecutiveMatches + 1
                : 1;
            track.LastSeenFrame = frameSequence;
            track.Matched = true;
            if (track.ConsecutiveMatches >= TrackConfirmationFrames)
                track.Confirmed = true;
        }

        double intervalMilliseconds = receiverHopSize * 1_000.0 / sampleRateHz;
        for (int index = tracks.Count - 1; index >= 0; index--)
        {
            SignalTrack track = tracks[index];
            if (!track.Confirmed && frameSequence - track.LastSeenFrame > UnconfirmedTrackAgeFrames)
            {
                tracks.RemoveAt(index);
                continue;
            }

            double noiseVariance = noisePower / windowEnergy;
            CarrierPowers carrierPowers = MeasureCarrierPowers(
                inputReal, inputImaginary, window, carrierWindow,
                track.OffsetHz, sampleRateHz);
            double recentSnrDb = 10 * Math.Log10(Math.Max(1e-24,
                carrierPowers.Recent / (carrierWindowEnergy * noiseVariance)));
            double integratedSnrDb = 10 * Math.Log10(Math.Max(1e-24,
                carrierPowers.Integrated / (windowEnergy * noiseVariance)));

            track.IntegratedPeakSnrDb = Math.Max(
                integratedSnrDb,
                track.IntegratedPeakSnrDb - IntegratedPeakDecayDbPerSecond *
                intervalMilliseconds / 1_000.0);
            bool useWeakSignalIntegration = track.IntegratedPeakSnrDb <
                minimumSnrDb + WeakSignalIntegrationMarginDb;
            double snrDb = useWeakSignalIntegration ? integratedSnrDb : recentSnrDb;
            // Weak carriers use the same full-frame coherent integration that
            // discovered them. Strong carriers retain the edge path
            // so high-speed dots and inter-element gaps stay sharply separated.
            double keyOnThreshold = minimumSnrDb;
            double threshold = track.Decoder.IsKeyDown
                ? keyOnThreshold - KeyReleaseHysteresisDb
                : keyOnThreshold;
            bool observedKeyDown = snrDb >= threshold;
            if (observedKeyDown != track.PendingKeyDown)
            {
                track.PendingKeyDown = observedKeyDown;
                track.PendingKeyDownFrames = 1;
            }
            else if (track.PendingKeyDownFrames < KeyStateConfirmationFrames)
            {
                track.PendingKeyDownFrames++;
            }

            bool isKeyDown = track.Decoder.IsKeyDown;
            if (track.PendingKeyDownFrames >= KeyStateConfirmationFrames)
                isKeyDown = track.PendingKeyDown;
            string appended = track.Decoder.Process(isKeyDown, intervalMilliseconds);
            if (track.Decoder.IsKeyDown)
                track.SentenceBoundaryEmitted = false;
            bool sentenceBoundary = !track.SentenceBoundaryEmitted &&
                track.Decoder.HasText &&
                track.Decoder.CurrentSilenceMilliseconds >= SentenceBoundarySilenceMilliseconds;
            if (sentenceBoundary) track.SentenceBoundaryEmitted = true;
            track.LastSnrDb = snrDb;

            if (track.Confirmed && (appended.Length > 0 || sentenceBoundary))
            {
                long center = metadata.Configuration.ChannelCenterFrequencyHz;
                var result = new CwDecodeResult(
                    track.Id,
                    metadata.Source.UtcTimestamp,
                    metadata.Source.StreamId,
                    center + (long)Math.Round(track.OffsetHz),
                    track.OffsetHz,
                    snrDb,
                    track.Decoder.SpeedWpm,
                    appended,
                    track.Decoder.Text,
                    IsSentenceBoundary: sentenceBoundary);
                TextDecoded?.Invoke(this, new CwDecodedEventArgs(result));
            }

            long maximumTrackAgeFrames = Math.Max(1,
                (long)sampleRateHz * MaximumTrackAgeSeconds / receiverHopSize);
            if (frameSequence - track.LastSeenFrame <= maximumTrackAgeFrames) continue;
            string flushed = track.Decoder.Flush();
            if (track.Confirmed && track.Decoder.HasText)
            {
                long center = metadata.Configuration.ChannelCenterFrequencyHz;
                TextDecoded?.Invoke(this, new CwDecodedEventArgs(new CwDecodeResult(
                    track.Id, metadata.Source.UtcTimestamp, metadata.Source.StreamId,
                    center + (long)Math.Round(track.OffsetHz), track.OffsetHz,
                    track.LastSnrDb, track.Decoder.SpeedWpm, flushed, track.Decoder.Text,
                    IsFinal: true)));
            }
            tracks.RemoveAt(index);
        }
    }

    private List<Peak> FindPeaks(
        double noisePower,
        double requiredSnrDb,
        int receiverBandwidthHz)
    {
        double threshold = noisePower * Math.Pow(10, requiredSnrDb / 10);
        peakScratch.Clear();
        for (int bin = 0; bin < receiverFftSize; bin++)
        {
            double offset = BinToOffset(bin);
            if (Math.Abs(offset) > receiverBandwidthHz / 2.0 - 20) continue;
            int left = (bin + receiverFftSize - 1) % receiverFftSize;
            int right = (bin + 1) % receiverFftSize;
            if (power[bin] < threshold || power[bin] < power[left] || power[bin] <= power[right])
                continue;

            double alpha = Math.Log(power[left]);
            double beta = Math.Log(power[bin]);
            double gamma = Math.Log(power[right]);
            double denominator = alpha - 2 * beta + gamma;
            double correction = Math.Abs(denominator) < 1e-12
                ? 0 : Math.Clamp(0.5 * (alpha - gamma) / denominator, -0.5, 0.5);
            peakScratch.Add(new Peak(
                offset + correction * sampleRateHz / receiverFftSize, power[bin]));
        }

        peakScratch.Sort(static (left, right) => right.Power.CompareTo(left.Power));
        selectedPeakScratch.Clear();
        foreach (Peak peak in peakScratch)
        {
            bool overlaps = false;
            for (int index = 0; index < selectedPeakScratch.Count; index++)
            {
                if (Math.Abs(selectedPeakScratch[index].OffsetHz - peak.OffsetHz) >=
                    MinimumCarrierSpacingHz) continue;
                overlaps = true;
                break;
            }
            if (overlaps) continue;
            selectedPeakScratch.Add(peak);
            if (selectedPeakScratch.Count == 32) break;
        }
        // The returned list is receiver-owned scratch storage and remains valid
        // only until the next Process/FindPeaks call.
        return selectedPeakScratch;
    }

    private SignalTrack? FindTrack(double offsetHz)
    {
        SignalTrack? best = null;
        double bestDistance = Math.Max(MinimumCarrierSpacingHz,
            sampleRateHz * 2.5 / receiverFftSize);
        foreach (SignalTrack track in tracks)
        {
            if (track.Matched) continue;
            double distance = Math.Abs(track.OffsetHz - offsetHz);
            if (distance >= bestDistance) continue;
            best = track;
            bestDistance = distance;
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ApplyFftWindow(
        float[] inputReal,
        float[] inputImaginary,
        float[] weights,
        float[] outputReal,
        float[] outputImaginary)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;
            for (; index <= inputReal.Length - width; index += width)
            {
                Vector<float> windowVector = new(weights, index);
                (new Vector<float>(inputReal, index) * windowVector).CopyTo(outputReal, index);
                (new Vector<float>(inputImaginary, index) * windowVector)
                    .CopyTo(outputImaginary, index);
            }
        }

        for (; index < inputReal.Length; index++)
        {
            outputReal[index] = inputReal[index] * weights[index];
            outputImaginary[index] = inputImaginary[index] * weights[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static CarrierPowers MeasureCarrierPowers(
        Complex[] samples,
        double[] integratedWeights,
        double[] recentWeights,
        double offsetHz,
        int sampleRateHz)
    {
        if (samples.Length != integratedWeights.Length || recentWeights.Length > samples.Length)
            throw new ArgumentException("Carrier-power buffers have inconsistent lengths.");
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        double angle = -2 * Math.PI * offsetHz / sampleRateHz;
        double stepReal = Math.Cos(angle);
        double stepImaginary = Math.Sin(angle);
        double oscillatorReal = 1;
        double oscillatorImaginary = 0;
        double integratedReal = 0;
        double integratedImaginary = 0;
        double recentReal = 0;
        double recentImaginary = 0;
        int recentStart = samples.Length - recentWeights.Length;

        for (int index = 0; index < recentStart; index++)
        {
            Complex sample = samples[index];
            double rotatedReal = sample.Real * oscillatorReal -
                sample.Imaginary * oscillatorImaginary;
            double rotatedImaginary = sample.Real * oscillatorImaginary +
                sample.Imaginary * oscillatorReal;
            integratedReal += rotatedReal * integratedWeights[index];
            integratedImaginary += rotatedImaginary * integratedWeights[index];
            double nextReal = oscillatorReal * stepReal -
                oscillatorImaginary * stepImaginary;
            oscillatorImaginary = oscillatorImaginary * stepReal +
                oscillatorReal * stepImaginary;
            oscillatorReal = nextReal;
        }

        for (int index = recentStart; index < samples.Length; index++)
        {
            Complex sample = samples[index];
            double rotatedReal = sample.Real * oscillatorReal -
                sample.Imaginary * oscillatorImaginary;
            double rotatedImaginary = sample.Real * oscillatorImaginary +
                sample.Imaginary * oscillatorReal;
            integratedReal += rotatedReal * integratedWeights[index];
            integratedImaginary += rotatedImaginary * integratedWeights[index];
            int recentIndex = index - recentStart;
            // Reusing the integrated oscillator only rotates the recent sum by
            // a constant phase, which leaves its squared magnitude unchanged.
            recentReal += rotatedReal * recentWeights[recentIndex];
            recentImaginary += rotatedImaginary * recentWeights[recentIndex];
            double nextReal = oscillatorReal * stepReal -
                oscillatorImaginary * stepImaginary;
            oscillatorImaginary = oscillatorImaginary * stepReal +
                oscillatorReal * stepImaginary;
            oscillatorReal = nextReal;
        }

        return new CarrierPowers(
            recentReal * recentReal + recentImaginary * recentImaginary,
            integratedReal * integratedReal + integratedImaginary * integratedImaginary);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static CarrierPowers MeasureCarrierPowers(
        float[] samplesReal,
        float[] samplesImaginary,
        double[] integratedWeights,
        double[] recentWeights,
        double offsetHz,
        int sampleRateHz)
    {
        if (samplesReal.Length != samplesImaginary.Length ||
            samplesReal.Length != integratedWeights.Length ||
            recentWeights.Length > samplesReal.Length)
            throw new ArgumentException("Carrier-power buffers have inconsistent lengths.");
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        double angle = -2 * Math.PI * offsetHz / sampleRateHz;
        double stepReal = Math.Cos(angle);
        double stepImaginary = Math.Sin(angle);
        double oscillatorReal = 1;
        double oscillatorImaginary = 0;
        double integratedReal = 0;
        double integratedImaginary = 0;
        double recentReal = 0;
        double recentImaginary = 0;
        int recentStart = samplesReal.Length - recentWeights.Length;

        for (int index = 0; index < recentStart; index++)
        {
            double sampleReal = samplesReal[index];
            double sampleImaginary = samplesImaginary[index];
            double rotatedReal = sampleReal * oscillatorReal -
                sampleImaginary * oscillatorImaginary;
            double rotatedImaginary = sampleReal * oscillatorImaginary +
                sampleImaginary * oscillatorReal;
            integratedReal += rotatedReal * integratedWeights[index];
            integratedImaginary += rotatedImaginary * integratedWeights[index];
            double nextReal = oscillatorReal * stepReal -
                oscillatorImaginary * stepImaginary;
            oscillatorImaginary = oscillatorImaginary * stepReal +
                oscillatorReal * stepImaginary;
            oscillatorReal = nextReal;
        }

        for (int index = recentStart; index < samplesReal.Length; index++)
        {
            double sampleReal = samplesReal[index];
            double sampleImaginary = samplesImaginary[index];
            double rotatedReal = sampleReal * oscillatorReal -
                sampleImaginary * oscillatorImaginary;
            double rotatedImaginary = sampleReal * oscillatorImaginary +
                sampleImaginary * oscillatorReal;
            integratedReal += rotatedReal * integratedWeights[index];
            integratedImaginary += rotatedImaginary * integratedWeights[index];
            int recentIndex = index - recentStart;
            recentReal += rotatedReal * recentWeights[recentIndex];
            recentImaginary += rotatedImaginary * recentWeights[recentIndex];
            double nextReal = oscillatorReal * stepReal -
                oscillatorImaginary * stepImaginary;
            oscillatorImaginary = oscillatorImaginary * stepReal +
                oscillatorReal * stepImaginary;
            oscillatorReal = nextReal;
        }

        return new CarrierPowers(
            recentReal * recentReal + recentImaginary * recentImaginary,
            integratedReal * integratedReal + integratedImaginary * integratedImaginary);
    }

    private double BinToOffset(int bin) =>
        (bin <= receiverFftSize / 2 ? bin : bin - receiverFftSize) *
        sampleRateHz / (double)receiverFftSize;

    private void ConfigureBandwidth(int receiverBandwidthHz)
    {
        int fftSize = GetFftSize(receiverBandwidthHz);
        if (fftSize == receiverFftSize) return;

        receiverFftSize = fftSize;
        receiverHopSize = GetHopSize(receiverBandwidthHz);
        inputReal = new float[receiverFftSize];
        inputImaginary = new float[receiverFftSize];
        spectrumReal = new float[receiverFftSize];
        spectrumImaginary = new float[receiverFftSize];
        fft = new Radix2Fft(receiverFftSize);
        power = new double[receiverFftSize];
        noiseScratch = new double[receiverFftSize];
        window = CreateWindow(receiverFftSize);
        fftWindow = CreateFloatWindow(receiverFftSize);
        carrierWindow = CreateWindow(receiverHopSize);
        windowEnergy = CalculateEnergy(window);
        carrierWindowEnergy = CalculateEnergy(carrierWindow);
        Reset();
    }

    private static int NormalizeReceiverBandwidth(int receiverBandwidthHz) =>
        CwBandwidth.Normalize(receiverBandwidthHz);

    private static double[] CreateWindow(int length)
    {
        var result = new double[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (result.Length - 1));
        return result;
    }

    private static float[] CreateFloatWindow(int length)
    {
        var result = new float[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = (float)(0.5 - 0.5 * Math.Cos(
                2 * Math.PI * index / (result.Length - 1)));
        return result;
    }

    private static double CalculateEnergy(ReadOnlySpan<double> values)
    {
        double energy = 0;
        foreach (double value in values) energy += value * value;
        return energy;
    }

    internal static double SelectNoisePower(Span<double> values)
    {
        if (values.IsEmpty)
            throw new ArgumentException("At least one noise sample is required.", nameof(values));

        int targetIndex = Math.Clamp(values.Length * 55 / 100, 0, values.Length - 1);
        bool ascending = true;
        bool descending = true;
        for (int index = 1; index < values.Length; index++)
        {
            int comparison = values[index].CompareTo(values[index - 1]);
            if (comparison < 0) ascending = false;
            if (comparison > 0) descending = false;
            if (!ascending && !descending) break;
        }
        if (ascending) return values[targetIndex];
        if (descending) return values[values.Length - 1 - targetIndex];

        int left = 0;
        int right = values.Length - 1;
        int depthLimit = 2 * (BitOperations.Log2((uint)values.Length) + 1);

        while (right - left > 16)
        {
            if (depthLimit-- == 0)
            {
                // Bound adversarial inputs with the runtime's introspective sort.
                values.Slice(left, right - left + 1).Sort();
                return values[targetIndex];
            }

            int middle = left + ((right - left) >> 1);
            SortPair(values, left, middle);
            SortPair(values, left, right);
            SortPair(values, middle, right);
            double pivot = values[middle];

            // A three-way partition finishes duplicate-heavy inputs in one pass.
            // CompareTo preserves Array.Sort's ordering for NaN and infinities.
            int lower = left;
            int current = left;
            int upper = right;
            while (current <= upper)
            {
                int comparison = values[current].CompareTo(pivot);
                if (comparison < 0)
                {
                    Swap(values, lower++, current++);
                }
                else if (comparison > 0)
                {
                    Swap(values, current, upper--);
                }
                else
                {
                    current++;
                }
            }

            if (targetIndex < lower)
                right = lower - 1;
            else if (targetIndex > upper)
                left = upper + 1;
            else
                return values[targetIndex];
        }

        for (int index = left + 1; index <= right; index++)
        {
            double value = values[index];
            int insertion = index;
            while (insertion > left && value.CompareTo(values[insertion - 1]) < 0)
            {
                values[insertion] = values[insertion - 1];
                insertion--;
            }
            values[insertion] = value;
        }
        return values[targetIndex];
    }

    private static void SortPair(Span<double> values, int left, int right)
    {
        if (values[right].CompareTo(values[left]) < 0)
            Swap(values, left, right);
    }

    private static void Swap(Span<double> values, int left, int right)
    {
        if (left == right) return;
        (values[left], values[right]) = (values[right], values[left]);
    }

    private sealed class SignalTrack
    {
        public SignalTrack(long id, double offsetHz, long frame, CwLanguage defaultLanguage)
        {
            Id = id;
            OffsetHz = offsetHz;
            LastSeenFrame = frame;
            Decoder = new MorseTimingDecoder { CurrentLanguage = defaultLanguage };
        }

        public long Id { get; }
        public MorseTimingDecoder Decoder { get; }
        public double OffsetHz { get; set; }
        public long LastSeenFrame { get; set; }
        public double LastSnrDb { get; set; }
        public bool Matched { get; set; }
        public int ConsecutiveMatches { get; set; } = 1;
        public bool Confirmed { get; set; }
        public bool PendingKeyDown { get; set; }
        public int PendingKeyDownFrames { get; set; }
        public double IntegratedPeakSnrDb { get; set; } = double.NegativeInfinity;
        public bool SentenceBoundaryEmitted { get; set; }
    }

    private readonly record struct Peak(double OffsetHz, double Power);

    internal readonly record struct CarrierPowers(double Recent, double Integrated);
}

public sealed class CwDecodedEventArgs(CwDecodeResult result) : EventArgs
{
    public CwDecodeResult Result { get; } = result;
}
