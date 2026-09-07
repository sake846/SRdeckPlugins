using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal readonly record struct EqualizedOneSegSymbol(
    // Reused by OfdmCarrierDecoder; consume before the next Process call.
    ComplexF[] Data,
    bool FrameEnded,
    int RelativeSymbolIndex,
    double MerDb,
    TmccParameters? Tmcc);

internal sealed class OfdmCarrierDecoder
{
    private readonly TmccDecoder tmcc = new();
    private ComplexF[] expectedPilots = [];
    private ComplexF[] activeBuffer = [];
    private ComplexF[] channelBuffer = [];
    private ComplexF[] equalizedBuffer = [];
    private ComplexF[] dataBuffer = [];
    private float[] fftMagnitudes = [];
    private int[] pilotPositions = [];
    private bool[][] excludedCarriers = [];
    private IsdbTMode configuredMode;
    private int activeCarrierCount;
    private int configuredFftSize;
    private int relativeSymbolIndex = -1;
    private int carrierOffset;
    private bool carrierOffsetLocked;
    private int symbolsSinceCarrierSearch;

    public TmccDecoder Tmcc => tmcc;
    internal int CarrierOffset => carrierOffset;
    internal long TmccAcquisitionRecoveries { get; private set; }

    public void Reset()
    {
        tmcc.Reset();
        configuredMode = IsdbTMode.Unknown;
        relativeSymbolIndex = -1;
        carrierOffset = 0;
        carrierOffsetLocked = false;
        symbolsSinceCarrierSearch = 0;
        TmccAcquisitionRecoveries = 0;
    }

    public EqualizedOneSegSymbol Process(OfdmSymbol symbol)
    {
        EnsureMode(symbol.Mode);
        if (symbol.TimingDiscontinuity)
        {
            relativeSymbolIndex = -1;
            carrierOffsetLocked = false;
            tmcc.Resynchronize();
        }
        ReadOnlySpan<ComplexF> fft = symbol.Spectrum.Span;
        for (int index = 0; index < configuredFftSize; index++)
            fftMagnitudes[index] = MathF.Sqrt(Math.Max(0, fft[index].MagnitudeSquared));

        bool tmccAcquisitionTimedOut = carrierOffsetLocked && relativeSymbolIndex >= 0 &&
            !tmcc.IsLocked &&
            symbolsSinceCarrierSearch >= IsdbTParameters.SymbolsPerFrame * 2;
        if (!carrierOffsetLocked || relativeSymbolIndex < 0 || tmccAcquisitionTimedOut)
        {
            (carrierOffset, relativeSymbolIndex, _) = FindCarrierAndSymbol(fft, symbol.Mode);
            carrierOffsetLocked = true;
            symbolsSinceCarrierSearch = 0;
            if (tmccAcquisitionTimedOut)
            {
                // Pilot tracking can remain locally plausible after acquiring a
                // channel-change transient at the wrong integer carrier or
                // four-symbol phase. TMCC is the stronger end-to-end check: if
                // two complete frames fail, repeat the global search on the now
                // stable signal instead of retaining the stale acquisition.
                TmccAcquisitionRecoveries++;
                tmcc.Resynchronize();
            }
        }
        else
        {
            relativeSymbolIndex = (relativeSymbolIndex + 1) & 3;
            (int tracked, double trackScore) = FindBestOffset(fft, symbol.Mode, relativeSymbolIndex,
                Math.Max(-12, carrierOffset - 1), Math.Min(12, carrierOffset + 1));
            if (trackScore < 0.05)
            {
                (carrierOffset, relativeSymbolIndex, _) = FindCarrierAndSymbol(fft, symbol.Mode);
                carrierOffsetLocked = true;
                symbolsSinceCarrierSearch = 0;
                tmcc.Resynchronize();
            }
            else
            {
                carrierOffset = tracked;
            }
        }

        ExtractActive(fft, carrierOffset);
        double mer = Equalize(relativeSymbolIndex);
        bool frameEnded = tmcc.ProcessSymbol(equalizedBuffer, symbol.Mode);
        symbolsSinceCarrierSearch++;
        ExtractData(relativeSymbolIndex);
        return new(dataBuffer, frameEnded, relativeSymbolIndex, mer, tmcc.Parameters);
    }

    private void EnsureMode(IsdbTMode mode)
    {
        if (configuredMode == mode) return;
        configuredMode = mode;
        tmcc.Reset();
        relativeSymbolIndex = -1;
        carrierOffsetLocked = false;
        symbolsSinceCarrierSearch = 0;
        TmccAcquisitionRecoveries = 0;
        expectedPilots = GenerateSegmentZeroPilotValues(mode);
        activeCarrierCount = IsdbTParameters.ActiveCarriers(mode);
        configuredFftSize = IsdbTParameters.FftSize(mode);
        int dataCarrierCount = IsdbTParameters.DataCarriers(mode);
        activeBuffer = new ComplexF[activeCarrierCount];
        channelBuffer = new ComplexF[activeCarrierCount];
        equalizedBuffer = new ComplexF[activeCarrierCount];
        dataBuffer = new ComplexF[dataCarrierCount];
        fftMagnitudes = new float[configuredFftSize];
        pilotPositions = new int[(activeCarrierCount + 11) / 12];
        excludedCarriers = CreateExcludedCarrierMasks(mode, activeCarrierCount);
    }

    private (int Offset, int Symbol, double Score) FindCarrierAndSymbol(
        ReadOnlySpan<ComplexF> fft, IsdbTMode mode)
    {
        int bestOffset = 0, bestSymbol = 0;
        double best = double.NegativeInfinity;
        for (int symbol = 0; symbol < 4; symbol++)
        {
            for (int offset = -12; offset <= 12; offset++)
            {
                double score = PilotCorrelation(fft, mode, symbol, offset);
                if (score <= best) continue;
                best = score;
                bestOffset = offset;
                bestSymbol = symbol;
            }
        }
        return (bestOffset, bestSymbol, best);
    }

    private (int Offset, double Score) FindBestOffset(ReadOnlySpan<ComplexF> fft, IsdbTMode mode,
        int symbol, int minimum, int maximum)
    {
        int bestOffset = carrierOffset;
        double best = double.NegativeInfinity;
        for (int offset = minimum; offset <= maximum; offset++)
        {
            double score = PilotCorrelation(fft, mode, symbol, offset);
            if (score <= best) continue;
            best = score;
            bestOffset = offset;
        }
        return (bestOffset, best);
    }

    private double PilotCorrelation(ReadOnlySpan<ComplexF> fft, IsdbTMode mode,
        int symbol, int offset)
    {
        int half = activeCarrierCount / 2;
        double real = 0, imaginary = 0, normalization = 0;

        // The scattered-pilot signs are known and repeat every twelve carriers.
        // Correlating adjacent pilot pairs after compensating their expected sign
        // is the symbol-index estimator described by ARIB implementations.  It is
        // considerably less sensitive to a frequency-selective channel than
        // minimizing differences between independently estimated channel taps.
        for (int nextCarrier = 12 + 3 * symbol; nextCarrier < activeCarrierCount; nextCarrier += 12)
        {
            int currentCarrier = nextCarrier - 12;
            int currentBin = WrapBin(currentCarrier - half + offset, configuredFftSize);
            int nextBin = WrapBin(nextCarrier - half + offset, configuredFftSize);
            if ((uint)currentBin >= (uint)fft.Length || (uint)nextBin >= (uint)fft.Length)
                return double.NegativeInfinity;

            ComplexF current = fft[currentBin];
            ComplexF next = fft[nextBin];
            ComplexF pair = next * current.Conjugate();
            float expectedSign = expectedPilots[nextCarrier].Real *
                expectedPilots[currentCarrier].Real >= 0 ? 1f : -1f;
            real += pair.Real * expectedSign;
            imaginary += pair.Imaginary * expectedSign;
            normalization += (double)fftMagnitudes[nextBin] * fftMagnitudes[currentBin];
        }
        return (real * real + imaginary * imaginary) /
            Math.Max(1e-20, normalization * normalization);
    }

    private static int WrapBin(int signedBin, int fftSize)
    {
        int bin = signedBin % fftSize;
        return bin < 0 ? bin + fftSize : bin;
    }

    private void ExtractActive(ReadOnlySpan<ComplexF> fft, int offset)
    {
        int half = activeCarrierCount / 2;
        for (int carrier = 0; carrier < activeCarrierCount; carrier++)
        {
            int signedBin = carrier - half + offset;
            int bin = signedBin < 0 ? configuredFftSize + signedBin : signedBin;
            bin %= configuredFftSize;
            if (bin < 0) bin += configuredFftSize;
            activeBuffer[carrier] = fft[bin];
        }
    }

    private double Equalize(int symbol)
    {
        if ((uint)symbol >= 4)
        {
            activeBuffer.CopyTo(equalizedBuffer, 0);
            return 0;
        }
        int pilotCount = 0;
        for (int carrier = 3 * symbol; carrier < activeCarrierCount; carrier += 12)
        {
            channelBuffer[carrier] = activeBuffer[carrier] / expectedPilots[carrier].Real;
            pilotPositions[pilotCount++] = carrier;
        }
        if (pilotCount == 0)
        {
            activeBuffer.CopyTo(equalizedBuffer, 0);
            return 0;
        }

        int first = pilotPositions[0];
        for (int carrier = 0; carrier <= first; carrier++) channelBuffer[carrier] = channelBuffer[first];
        for (int pilot = 0; pilot + 1 < pilotCount; pilot++)
        {
            int left = pilotPositions[pilot];
            int right = pilotPositions[pilot + 1];
            for (int carrier = left + 1; carrier < right; carrier++)
            {
                float ratio = (carrier - left) / (float)(right - left);
                channelBuffer[carrier] = channelBuffer[left] * (1f - ratio) +
                    channelBuffer[right] * ratio;
            }
        }
        int last = pilotPositions[pilotCount - 1];
        for (int carrier = last; carrier < activeCarrierCount; carrier++)
            channelBuffer[carrier] = channelBuffer[last];

        for (int carrier = 0; carrier < activeCarrierCount; carrier++)
            equalizedBuffer[carrier] = ComplexF.Divide(activeBuffer[carrier], channelBuffer[carrier]);

        double error = 0, reference = 0;
        for (int index = 1; index + 1 < pilotCount; index++)
        {
            int position = pilotPositions[index];
            ComplexF estimate = (channelBuffer[pilotPositions[index - 1]] +
                channelBuffer[pilotPositions[index + 1]]) * 0.5f;
            ComplexF predicted = estimate * expectedPilots[position].Real;
            error += (activeBuffer[position] - predicted).MagnitudeSquared;
            reference += predicted.MagnitudeSquared;
        }
        return 10.0 * Math.Log10(Math.Max(1e-12, reference) / Math.Max(1e-12, error));
    }

    private void ExtractData(int symbol)
    {
        if ((uint)symbol >= 4)
        {
            Array.Clear(dataBuffer);
            return;
        }
        bool[] excluded = excludedCarriers[symbol];
        int output = 0;
        for (int carrier = 0; carrier < activeCarrierCount; carrier++)
        {
            if (excluded[carrier]) continue;
            if (output < dataBuffer.Length) dataBuffer[output++] = equalizedBuffer[carrier];
        }
        if (output != dataBuffer.Length)
            throw new InvalidOperationException($"ISDB-T carrier map produced {output} data carriers instead of {dataBuffer.Length}.");
    }

    private static bool[][] CreateExcludedCarrierMasks(IsdbTMode mode, int activeCount)
    {
        var result = new bool[4][];
        ReadOnlySpan<int> tmccCarriers = IsdbTParameters.TmccCarriers(mode);
        ReadOnlySpan<int> acCarriers = IsdbTParameters.AcCarriers(mode);
        for (int symbol = 0; symbol < result.Length; symbol++)
        {
            bool[] mask = result[symbol] = new bool[activeCount];
            for (int carrier = 3 * symbol; carrier < activeCount; carrier += 12)
                mask[carrier] = true;
            foreach (int carrier in tmccCarriers) mask[carrier] = true;
            foreach (int carrier in acCarriers) mask[carrier] = true;
        }
        return result;
    }

    private static ComplexF[] GenerateSegmentZeroPilotValues(IsdbTMode mode)
    {
        int multiplier = IsdbTParameters.ModeMultiplier(mode);
        int carriersPerSegment = 108 * multiplier;
        int globalOffset = 6 * carriersPerSegment;
        int required = globalOffset + carriersPerSegment;
        int register = (1 << 11) - 1;
        var result = new ComplexF[carriersPerSegment];
        for (int carrier = 0; carrier < required; carrier++)
        {
            int output = register & 1;
            int newBit = ((register >> 2) ^ register) & 1;
            register = (register >> 1) | (newBit << 10);
            if (carrier >= globalOffset)
                result[carrier - globalOffset] = new ComplexF(output == 0 ? 4f / 3f : -4f / 3f, 0);
        }
        return result;
    }
}
