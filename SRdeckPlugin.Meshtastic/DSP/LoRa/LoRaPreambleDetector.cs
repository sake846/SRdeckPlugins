using System;
using System.Collections.Generic;
using SRdeck.DSP;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Streaming LoRa acquisition state machine. It detects repeated up-chirps,
/// corrects the coarse symbol boundary, verifies the two sync-word symbols,
/// then confirms the first two down-chirps of the SFD.
/// </summary>
internal sealed partial class LoRaPreambleDetector
{
    private const float UpChirpThresholdDb = 11.0f;
    private const float DownChirpThresholdDb = 9.0f;
    private const int RequiredConsecutiveSymbols = 5;
    private const int PeakBinTolerance = 4;

    private enum AcquisitionState
    {
        Search,
        Aligning,
        Preamble,
        SyncLow,
        Sfd,
        Header,
        Payload
    }

    private readonly int _sampleRateHz;
    private readonly int _bandwidthHz;
    private readonly int _spreadingFactor;
    private readonly byte _syncWord;
    private readonly int _symbolSamples;
    private readonly int _symbolValues;
    private readonly int _samplesPerChip;
    private readonly float _expectedCenterOffsetHz;
    private readonly int _expectedCenterBin;
    private readonly Complex[] _ring;
    private readonly Complex[] _downChirp;
    private readonly FastFourierTransform _fft;
    private int _writeIndex;
    private int _sampleCount;
    private int _samplesSinceAnalysis;
    private int _lastPeakBin = -1;
    private int _preamblePeakBin = -1;
    private int _consecutiveSymbols;
    private int _missedSymbols;
    private int _syncDirection;
    private int _sfdSymbols;
    private readonly ushort[] _headerSymbols = new ushort[8];
    private int _headerSymbolCount;
    private readonly ushort[] _payloadBlockSymbols = new ushort[8];
    private int _payloadBlockSymbolCount;
    private List<byte>? _payloadNibbles;
    private LoRaExplicitHeader? _activeHeader;
    private int _payloadCorrectedCodewords;
    private float _lastDownPeakHz;
    private int _consecutiveLowSnrSymbols;
    private bool _reported;
    private AcquisitionState _state;

    public LoRaPreambleDetector(int sampleRateHz, int bandwidthHz, int spreadingFactor,
        byte syncWord = MeshtasticJpLongFastProfile.SyncWord, float expectedCenterOffsetHz = 0)
    {
        _sampleRateHz = sampleRateHz;
        _bandwidthHz = bandwidthHz;
        _spreadingFactor = spreadingFactor;
        _syncWord = syncWord;
        _expectedCenterOffsetHz = expectedCenterOffsetHz;
        _symbolValues = 1 << spreadingFactor;
        double exactSamples = sampleRateHz * (_symbolValues / (double)bandwidthHz);
        _symbolSamples = checked((int)Math.Round(exactSamples));
        if (_symbolSamples < 2 || (_symbolSamples & (_symbolSamples - 1)) != 0)
        {
            throw new ArgumentException("The LoRa detector requires a power-of-two number of samples per symbol.");
        }
        if (_symbolSamples % _symbolValues != 0)
        {
            throw new ArgumentException("The LoRa detector requires an integer number of samples per chip.");
        }

        _samplesPerChip = _symbolSamples / _symbolValues;
        _expectedCenterBin = (int)MathF.Round(expectedCenterOffsetHz * _symbolSamples / sampleRateHz);
        _ring = new Complex[_symbolSamples * 2];
        _downChirp = CreateDownChirp(_symbolSamples, sampleRateHz, bandwidthHz);
        _fft = new FastFourierTransform(_symbolSamples);
    }

    public event Action<LoRaPreambleDetection>? PreambleDetected;
    public event Action<LoRaFrameSynchronization>? FrameSynchronized;
    public event Action<LoRaExplicitHeader>? ExplicitHeaderDecoded;
    public event Action<LoRaPayloadFrame>? PayloadDecoded;
    public event Action<LoRaAcquisitionDiagnostic>? AcquisitionDiagnostic;

    public void Reset()
    {
        Array.Clear(_ring);
        _writeIndex = 0;
        _sampleCount = 0;
        _samplesSinceAnalysis = 0;
        ResetAcquisition();
    }

    public void Push(float i, float q)
    {
        PushCore(i, q);
    }

    public void PushBlock(ReadOnlySpan<float> samplesI, ReadOnlySpan<float> samplesQ)
    {
        int count = Math.Min(samplesI.Length, samplesQ.Length);
        for (int index = 0; index < count; index++)
            PushCore(samplesI[index], samplesQ[index]);
    }

    private void PushCore(float i, float q)
    {
        _ring[_writeIndex] = new Complex { X = i, Y = q };
        _ring[_writeIndex + _symbolSamples] = new Complex { X = i, Y = q };
        _writeIndex++;
        if (_writeIndex == _symbolSamples) _writeIndex = 0;
        if (_sampleCount < _symbolSamples) _sampleCount++;
        _samplesSinceAnalysis++;

        if (_sampleCount < _symbolSamples || _samplesSinceAnalysis < _symbolSamples) return;
        _samplesSinceAnalysis = 0;
        AnalyzeSymbol();
    }

    private void AnalyzeSymbol()
    {
        SpectrumPeak upPeak = CalculatePeak(dechirpUpChirp: true);
        switch (_state)
        {
            case AcquisitionState.Search:
                ProcessSearch(upPeak);
                break;
            case AcquisitionState.Aligning:
                ProcessAlignedPreamble(upPeak);
                break;
            case AcquisitionState.Preamble:
                ProcessPreambleOrSyncHigh(upPeak);
                break;
            case AcquisitionState.SyncLow:
                ProcessSyncLow(upPeak);
                break;
            case AcquisitionState.Sfd:
                ProcessSfd(CalculatePeak(dechirpUpChirp: false));
                break;
            case AcquisitionState.Header:
                ProcessHeader(upPeak);
                break;
            case AcquisitionState.Payload:
                ProcessPayload(upPeak);
                break;
        }
    }


    private SpectrumPeak CalculatePeak(bool dechirpUpChirp)
    {
        Complex[] fftInput = _fft.InputData;
        double totalPower = 0.0;
        for (int n = 0; n < _symbolSamples; n++)
        {
            Complex sample = _ring[_writeIndex + n];
            Complex reference = _downChirp[n];
            if (!dechirpUpChirp) reference.Y = -reference.Y;
            float x = sample.X * reference.X - sample.Y * reference.Y;
            float y = sample.X * reference.Y + sample.Y * reference.X;
            fftInput[n] = new Complex { X = x, Y = y };
            totalPower += (x * x) + (y * y);
        }

        if (totalPower < 1e-10) return new SpectrumPeak(0, float.NegativeInfinity, 0.0f);

        _fft.ExecutePower(System.Numerics.BitOperations.Log2((uint)_symbolSamples));
        float[] spectrum = _fft.OutputData;
        int peakBin = 0;
        float peakPower = float.NegativeInfinity;
        double sumPower = 0.0;
        for (int i = 0; i < spectrum.Length; i++)
        {
            float power = spectrum[i];
            if (power > peakPower)
            {
                peakPower = power;
                peakBin = i;
            }
            sumPower += power;
        }

        int len = spectrum.Length;
        int leftBin = (peakBin - 1 + len) % len;
        int rightBin = (peakBin + 1) % len;
        float y0 = spectrum[leftBin];
        float y1 = spectrum[peakBin];
        float y2 = spectrum[rightBin];

        // A dechirped tone halfway between FFT bins splits almost equally across
        // two bins. Combining the stronger adjacent bin recovers up to 3 dB of
        // acquisition sensitivity while the repeated-bin preamble test still
        // rejects isolated noise peaks.
        float adjacentPower = Math.Max(y0, y2);
        double signalPower = peakPower + adjacentPower;
        double averageOtherPower = Math.Max(
            1e-30,
            (sumPower - signalPower) / Math.Max(1, spectrum.Length - 2));
        float peakToAverageDb = (float)(
            10.0 * Math.Log10(Math.Max(1e-30, signalPower) / averageOtherPower));

        float delta = 0.0f;
        float denom = y0 - (2.0f * y1) + y2;
        if (Math.Abs(denom) > 1e-12f)
        {
            delta = 0.5f * (y0 - y2) / denom;
            if (delta > 0.5f) delta = 0.5f;
            else if (delta < -0.5f) delta = -0.5f;
        }
        float interpolatedBin = peakBin + delta;
        float interpolatedFreqHz = (interpolatedBin - (len / 2.0f)) * (_sampleRateHz / (float)len);

        return new SpectrumPeak(peakBin, peakToAverageDb, interpolatedFreqHz);
    }

    private void RegisterSearchMiss()
    {
        _missedSymbols++;
        if (_missedSymbols < 3) return;
        ResetAcquisition();
    }

    private void RegisterFrameMiss(
        string stage,
        string message,
        SpectrumPeak peak,
        int? observedSymbol,
        int? expectedSymbol)
    {
        _missedSymbols++;
        if (_missedSymbols < 2) return;
        ReportDiagnostic(stage, message, peak, observedSymbol, expectedSymbol, true);
        ResetAcquisition();
    }

    private void ReportDiagnostic(
        string stage,
        string message,
        SpectrumPeak peak,
        int? observedSymbol,
        int? expectedSymbol,
        bool isFailure) =>
        AcquisitionDiagnostic?.Invoke(new LoRaAcquisitionDiagnostic(
            DateTimeOffset.UtcNow,
            stage,
            message,
            peak.PeakToAverageDb,
            peak.FrequencyHz,
            observedSymbol,
            expectedSymbol,
            isFailure));

    private void ResetAcquisition()
    {
        _state = AcquisitionState.Search;
        _lastPeakBin = -1;
        _preamblePeakBin = -1;
        _consecutiveSymbols = 0;
        _missedSymbols = 0;
        _syncDirection = 0;
        _sfdSymbols = 0;
        _headerSymbolCount = 0;
        _payloadBlockSymbolCount = 0;
        _payloadNibbles = null;
        _activeHeader = null;
        _payloadCorrectedCodewords = 0;
        _consecutiveLowSnrSymbols = 0;
        _reported = false;
    }

    private int ToSignedBin(int shiftedBin) => shiftedBin - (_symbolSamples / 2);

    private float BinToFrequencyHz(int shiftedBin) => ToSignedBin(shiftedBin) * (_sampleRateHz / (float)_symbolSamples);

    private int SymbolDelta(int bin, int referenceBin)
    {
        int delta = Mod(ToSignedBin(bin) - ToSignedBin(referenceBin), _symbolValues);
        if (delta >= _symbolValues / 2) delta -= _symbolValues;
        return delta;
    }

    private static int DecodeSyncNibble(int nibble)
    {
        int signedNibble = nibble >= 8 ? nibble - 16 : nibble;
        return signedNibble << 3;
    }

    private static int CircularBinDistance(int a, int b, int size)
    {
        int distance = Math.Abs(a - b);
        return Math.Min(distance, size - distance);
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static Complex[] CreateDownChirp(int sampleCount, int sampleRateHz, int bandwidthHz)
    {
        var result = new Complex[sampleCount];
        double symbolSeconds = sampleCount / (double)sampleRateHz;
        for (int n = 0; n < sampleCount; n++)
        {
            double t = n / (double)sampleRateHz;
            double upChirpPhase = 2.0 * Math.PI * ((-bandwidthHz * 0.5 * t) + (bandwidthHz * t * t / (2.0 * symbolSeconds)));
            result[n] = new Complex
            {
                X = (float)Math.Cos(upChirpPhase),
                Y = (float)-Math.Sin(upChirpPhase)
            };
        }
        return result;
    }

    private readonly record struct SpectrumPeak(int Bin, float PeakToAverageDb, float FrequencyHz);
}
