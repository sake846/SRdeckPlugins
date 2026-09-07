using System;
using System.Collections.Generic;
using System.Diagnostics;
using SRdeck.DSP;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Streaming LoRa acquisition state machine. It detects repeated up-chirps,
/// corrects the symbol boundary to sample resolution, verifies the two sync-word symbols,
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
    private LoRaFinePeakEstimator? _finePeakEstimator;
    private readonly LoRaDetectorMetricsAccumulator? _metrics;
    private readonly LoRaSoftDecodingOptions? _softOptions;
    private readonly LoRaCrcListDecodingOptions? _crcListOptions;
    private readonly LoRaSearchThresholdPolicy? _searchThresholdPolicy;
    private readonly LoRaSearchEnergyGate _searchEnergyGate;
    private readonly LoRaSnrEstimator _snrEstimator;
    private readonly float _fixedSearchThresholdDb = UpChirpThresholdDb;
    private double _ringPower;
    private int _writeIndex;
    private int _sampleCount;
    private int _samplesSinceAnalysis;
    private int _lastPeakBin = -1;
    private float _preamblePeakBin = float.NaN;
    private int _consecutiveSymbols;
    private int _missedSymbols;
    private int _syncDirection;
    private int _sfdSymbols;
    private readonly ushort[] _headerSymbols = new ushort[8];
    private readonly LoRaSoftSymbol[]? _headerSoftSymbols;
    private int _headerSymbolCount;
    private readonly ushort[] _payloadBlockSymbols = new ushort[8];
    private readonly LoRaSoftSymbol[]? _payloadBlockSoftSymbols;
    private int _payloadBlockSymbolCount;
    private List<byte>? _payloadNibbles;
    private List<LoRaSoftSymbol>? _payloadSoftSymbols;
    private LoRaExplicitHeader? _activeHeader;
    private int _payloadCorrectedCodewords;
    private float _lastDownPeakHz;
    private float _sfdDownPeakHzSum;
    private float _carrierFrequencyCorrectionHz;
    private int _frameTimingCorrectionSamples;
    private int _consecutiveLowSnrSymbols;
    private bool _reported;
    private AcquisitionState _state;

    public LoRaPreambleDetector(int sampleRateHz, int bandwidthHz, int spreadingFactor,
        byte syncWord = MeshtasticJpLongFastProfile.SyncWord, float expectedCenterOffsetHz = 0,
        bool enableMetrics = false, LoRaSoftDecodingOptions? softDecoding = null,
        LoRaCrcListDecodingOptions? crcListDecoding = null, LoRaSearchThresholdOptions? searchThresholdOptions = null,
        LoRaSearchEnergyGateOptions? searchEnergyGateOptions = null, int? inputNoiseBandwidthHz = null)
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
        _snrEstimator = new LoRaSnrEstimator(_symbolSamples, bandwidthHz, inputNoiseBandwidthHz ?? sampleRateHz);
        _expectedCenterBin = (int)MathF.Round(expectedCenterOffsetHz * _symbolSamples / sampleRateHz);
        _ring = new Complex[_symbolSamples * 2];
        _downChirp = CreateDownChirp(_symbolSamples, sampleRateHz, bandwidthHz);
        _fft = new FastFourierTransform(_symbolSamples);
        if (enableMetrics) _metrics = new LoRaDetectorMetricsAccumulator();
        if (softDecoding?.DecisionMode == LoRaFecDecisionMode.MaxLog)
        {
            _softOptions = softDecoding;
            _headerSoftSymbols = new LoRaSoftSymbol[8];
            _payloadBlockSoftSymbols = new LoRaSoftSymbol[8];
        }
        if (crcListDecoding is not null && _softOptions is null)
            throw new ArgumentException("CRC-aided list decoding requires max-log soft decoding.", nameof(crcListDecoding));
        _crcListOptions = crcListDecoding;
        _searchEnergyGate = new LoRaSearchEnergyGate(
            searchEnergyGateOptions ?? LoRaSearchEnergyGateOptions.Default);
        if (searchThresholdOptions is not null)
        {
            if (!float.IsFinite(searchThresholdOptions.FixedThresholdDb))
                throw new ArgumentOutOfRangeException(nameof(searchThresholdOptions));
            _fixedSearchThresholdDb = searchThresholdOptions.FixedThresholdDb;
            if (searchThresholdOptions.Mode == LoRaSearchThresholdMode.AdaptiveTemporalQuantile)
                _searchThresholdPolicy = new LoRaSearchThresholdPolicy(searchThresholdOptions);
        }
    }

    public event Action<LoRaPreambleDetection>? PreambleDetected;
    public event Action<LoRaFrameSynchronization>? FrameSynchronized;
    public event Action<LoRaExplicitHeader>? ExplicitHeaderDecoded;
    public event Action<LoRaPayloadFrame>? PayloadDecoded;
    public event Action<LoRaAcquisitionDiagnostic>? AcquisitionDiagnostic;
    public event Action<LoRaCrcListDecodingDiagnostic>? CrcListDecodingCompleted;
    internal event Action<LoRaSearchPeakObservation>? SearchPeakObserved;

    public void Reset()
    {
        Array.Clear(_ring);
        _writeIndex = 0;
        _sampleCount = 0;
        _samplesSinceAnalysis = 0;
        _ringPower = 0;
        _searchEnergyGate.Reset();
        _searchThresholdPolicy?.Reset();
        ResetAcquisition();
    }

    public void Push(float i, float q)
    {
        if (_metrics is not null) _metrics.InputSamples++;
        PushCore(i, q);
    }

    public void PushBlock(ReadOnlySpan<float> samplesI, ReadOnlySpan<float> samplesQ)
    {
        int count = Math.Min(samplesI.Length, samplesQ.Length);
        if (_metrics is not null) _metrics.InputSamples += count;
        for (int index = 0; index < count; index++)
            PushCore(samplesI[index], samplesQ[index]);
    }

    internal LoRaDetectorMetricsSnapshot GetMetricsSnapshot() =>
        _metrics?.Snapshot() ?? LoRaDetectorMetricsSnapshot.Disabled;

    internal void ResetMetrics() => _metrics?.Reset();

    internal void ResetAcquisitionForTest() => ResetAcquisition();

    internal float CurrentSearchThresholdDb =>
        _searchThresholdPolicy?.CurrentThresholdDb ?? _fixedSearchThresholdDb;

    private void PushCore(float i, float q)
    {
        if (_searchEnergyGate.Enabled)
        {
            Complex previous = _ring[_writeIndex];
            _ringPower += ((double)i * i) + ((double)q * q) -
                          ((double)previous.X * previous.X) - ((double)previous.Y * previous.Y);
        }
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
        if (_metrics is null)
        {
            AnalyzeSymbolCore();
            return;
        }

        AcquisitionState measuredState = _state;
        long started = Stopwatch.GetTimestamp();
        AnalyzeSymbolCore();
        _metrics.RecordAnalysis((int)measuredState, Stopwatch.GetTimestamp() - started);
    }

    private void AnalyzeSymbolCore()
    {
        if (_state == AcquisitionState.Search && ShouldSkipSearchFft())
        {
            if (_metrics is not null) _metrics.FftSkips++;
            ProcessSearch(new SpectrumPeak(0, 0.0f, float.NegativeInfinity, 0.0f));
            ObserveSearchNoisePower();
            return;
        }

        SpectrumPeak upPeak = CalculatePeak(dechirpUpChirp: true);
        switch (_state)
        {
            case AcquisitionState.Search:
                bool candidateWasActive = _consecutiveSymbols > 0;
                ProcessSearch(upPeak);
                if (!candidateWasActive && _consecutiveSymbols == 0)
                    ObserveSearchNoisePower();
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

    private bool ShouldSkipSearchFft()
    {
        double averagePower = Math.Max(0, _ringPower) / _symbolSamples;
        return _searchEnergyGate.ShouldSkip(averagePower, _consecutiveSymbols > 0);
    }

    private void ObserveSearchNoisePower()
    {
        double averagePower = Math.Max(0, _ringPower) / _symbolSamples;
        _searchEnergyGate.ObserveNoise(averagePower);
    }


    private SpectrumPeak CalculatePeak(bool dechirpUpChirp)
    {
        Complex[] fftInput = _fft.InputData;
        double totalPower = 0.0;
        if (_carrierFrequencyCorrectionHz == 0)
        {
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
        }
        else
        {
            double carrierI = 1.0;
            double carrierQ = 0.0;
            double carrierStep = -2.0 * Math.PI * _carrierFrequencyCorrectionHz / _sampleRateHz;
            double carrierStepI = Math.Cos(carrierStep);
            double carrierStepQ = Math.Sin(carrierStep);
            for (int n = 0; n < _symbolSamples; n++)
            {
                Complex sample = _ring[_writeIndex + n];
                float correctedSampleI = (float)((sample.X * carrierI) - (sample.Y * carrierQ));
                float correctedSampleQ = (float)((sample.X * carrierQ) + (sample.Y * carrierI));
                double nextCarrierI = (carrierI * carrierStepI) - (carrierQ * carrierStepQ);
                carrierQ = (carrierI * carrierStepQ) + (carrierQ * carrierStepI);
                carrierI = nextCarrierI;
                Complex reference = _downChirp[n];
                if (!dechirpUpChirp) reference.Y = -reference.Y;
                float x = correctedSampleI * reference.X - correctedSampleQ * reference.Y;
                float y = correctedSampleI * reference.Y + correctedSampleQ * reference.X;
                fftInput[n] = new Complex { X = x, Y = y };
                totalPower += (x * x) + (y * y);
            }
        }

        if (totalPower < 1e-10)
        {
            if (_metrics is not null) _metrics.FftSkips++;
            return new SpectrumPeak(0, 0.0f, float.NegativeInfinity, 0.0f);
        }

        // Leave the continuously running search and its thresholds unchanged.
        // Refine the aligned reference and every subsequent symbol consistently.
        // The SFD's unused up-chirp FFT does not need refinement.
        bool refinePeak = _state != AcquisitionState.Search &&
                          (_state != AcquisitionState.Sfd || !dechirpUpChirp);
        if (refinePeak)
        {
            _finePeakEstimator ??= new LoRaFinePeakEstimator(_symbolSamples);
            _finePeakEstimator.Prepare(fftInput);
        }
        if (_metrics is not null) _metrics.FftExecutions++;
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
        float interpolatedBin = refinePeak ? _finePeakEstimator!.Interpolate(spectrum, peakBin) : peakBin + delta;
        float interpolatedFreqHz = (interpolatedBin - (len / 2.0f)) * (_sampleRateHz / (float)len);

        SpectrumCandidate[]? candidates = _softOptions is not null &&
                                          _state is AcquisitionState.Header or AcquisitionState.Payload
            ? FindSpectrumCandidates(spectrum, peakBin)
            : null;
        return new SpectrumPeak(peakBin, interpolatedBin, peakToAverageDb, interpolatedFreqHz, candidates);
    }

    private SpectrumCandidate[] FindSpectrumCandidates(float[] spectrum, int peakBin)
    {
        int candidateCount = Math.Min(_softOptions!.CandidateCount, spectrum.Length);
        var candidates = new SpectrumCandidate[candidateCount];
        int selectedCount = 0;
        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            int selectedBin = candidateIndex == 0 ? peakBin : -1;
            float selectedPower = candidateIndex == 0 ? spectrum[peakBin] : float.NegativeInfinity;
            if (candidateIndex > 0)
            {
                for (int bin = 0; bin < spectrum.Length; bin++)
                {
                    bool tooClose = false;
                    for (int prior = 0; prior < selectedCount; prior++)
                    {
                        if (CircularBinDistance(bin, candidates[prior].Bin, spectrum.Length) < _samplesPerChip)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose || spectrum[bin] <= selectedPower) continue;
                    selectedBin = bin;
                    selectedPower = spectrum[bin];
                }
            }
            if (selectedBin < 0) break;

            float interpolatedBin = _finePeakEstimator!.Interpolate(spectrum, selectedBin);
            float logLikelihood = 10.0f * MathF.Log10(Math.Max(1e-30f, selectedPower));
            float bestLogLikelihood = selectedCount == 0
                ? logLikelihood
                : candidates[0].LogLikelihood;
            candidates[selectedCount++] = new SpectrumCandidate(
                selectedBin,
                interpolatedBin,
                selectedPower,
                logLikelihood,
                Math.Max(0, bestLogLikelihood - logLikelihood));
        }
        return selectedCount == candidates.Length ? candidates : candidates[..selectedCount];
    }

    private LoRaSoftSymbol CreateSoftSymbol(SpectrumPeak peak, bool reducedRate)
    {
        SpectrumCandidate[] candidates = peak.Candidates
            ?? throw new InvalidOperationException("Soft FFT candidates were not retained.");
        var mapped = new List<LoRaSymbolCandidate>(candidates.Length);
        foreach (SpectrumCandidate candidate in candidates)
        {
            float relativeRawSymbol = Mod(
                ToSignedBin(candidate.InterpolatedBin) - ToSignedBin(_preamblePeakBin),
                _symbolValues);
            int binarySymbol = reducedRate
                ? Mod((int)MathF.Round((relativeRawSymbol - 1.0f) / 4.0f,
                    MidpointRounding.AwayFromZero), _symbolValues / 4)
                : Mod((int)MathF.Round(relativeRawSymbol - 1.0f,
                    MidpointRounding.AwayFromZero), _symbolValues);
            ushort graySymbol = (ushort)(binarySymbol ^ (binarySymbol >> 1));
            bool duplicate = false;
            for (int index = 0; index < mapped.Count; index++)
            {
                if (mapped[index].Symbol != graySymbol) continue;
                duplicate = true;
                break;
            }
            if (duplicate) continue;
            mapped.Add(new LoRaSymbolCandidate(
                graySymbol,
                candidate.Power,
                candidate.LogLikelihood,
                candidate.RelativeCost));
        }

        return new LoRaSoftSymbol(
            mapped.ToArray(),
            peak.InterpolatedBin,
            _carrierFrequencyCorrectionHz,
            _frameTimingCorrectionSamples,
            WasClipped: false);
    }

    private void RegisterSearchMiss()
    {
        _missedSymbols++;
        if (_missedSymbols < 3) return;
        if (_metrics is not null && _consecutiveSymbols > 0 && !_reported)
            _metrics.SearchCandidateRejects++;
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
        _snrEstimator.Reset();
        _state = AcquisitionState.Search;
        _lastPeakBin = -1;
        _preamblePeakBin = float.NaN;
        _consecutiveSymbols = 0;
        _missedSymbols = 0;
        _syncDirection = 0;
        _sfdSymbols = 0;
        _headerSymbolCount = 0;
        _payloadBlockSymbolCount = 0;
        _payloadNibbles = null;
        _payloadSoftSymbols = null;
        _activeHeader = null;
        _payloadCorrectedCodewords = 0;
        _lastDownPeakHz = 0;
        _sfdDownPeakHzSum = 0;
        _carrierFrequencyCorrectionHz = 0;
        _frameTimingCorrectionSamples = 0;
        _consecutiveLowSnrSymbols = 0;
        _reported = false;
    }

    private int ToSignedBin(int shiftedBin) => shiftedBin - (_symbolSamples / 2);

    private float ToSignedBin(float shiftedBin) => shiftedBin - (_symbolSamples / 2.0f);

    private float BinToFrequencyHz(float shiftedBin) => ToSignedBin(shiftedBin) * (_sampleRateHz / (float)_symbolSamples);

    private float FrequencyToShiftedBin(float frequencyHz) =>
        Mod((frequencyHz * _symbolSamples / _sampleRateHz) + (_symbolSamples / 2.0f), _symbolSamples);

    private float SymbolDelta(float bin, float referenceBin)
    {
        float delta = Mod(ToSignedBin(bin) - ToSignedBin(referenceBin), _symbolValues);
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

    private static float Mod(float value, int modulus)
    {
        float result = value % modulus;
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

    private readonly record struct SpectrumPeak(
        int Bin,
        float InterpolatedBin,
        float PeakToAverageDb,
        float FrequencyHz,
        SpectrumCandidate[]? Candidates = null);

    private readonly record struct SpectrumCandidate(
        int Bin,
        float InterpolatedBin,
        float Power,
        float LogLikelihood,
        float RelativeCost);
}
