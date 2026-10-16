
// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

internal sealed partial class LoRaPreambleDetector
{
    private void ProcessSearch(SpectrumPeak peak)
    {
        bool candidateActive = _consecutiveSymbols > 0;
        LoRaSearchThresholdDecision decision = _searchThresholdPolicy?.Evaluate(
            peak.PeakToAverageDb, candidateActive) ??
            new LoRaSearchThresholdDecision(_fixedSearchThresholdDb,
                peak.PeakToAverageDb >= _fixedSearchThresholdDb);
        SearchPeakObserved?.Invoke(new LoRaSearchPeakObservation(
            peak.PeakToAverageDb,
            decision.ThresholdDb,
            decision.Accepted,
            _searchThresholdPolicy is not null && !candidateActive &&
            float.IsFinite(peak.PeakToAverageDb)));
        if (!decision.Accepted)
        {
            _snrEstimator.BreakSequence();
            RegisterSearchMiss();
            return;
        }

        if (_lastPeakBin >= 0 && CircularBinDistance(peak.Bin, _lastPeakBin, _symbolSamples) <= PeakBinTolerance)
        {
            _consecutiveSymbols++;
        }
        else
        {
            if (_metrics is not null && _consecutiveSymbols > 0)
                _metrics.SearchCandidateRejects++;
            _snrEstimator.Reset();
            _consecutiveSymbols = 1;
        }
        // Even before fine alignment, consecutive preamble windows repeat at
        // exactly one-symbol spacing. Retain these pairs for late acquisition.
        _snrEstimator.Observe(_ring.AsSpan(_writeIndex, _symbolSamples));
        _lastPeakBin = peak.Bin;
        _missedSymbols = 0;

        if (_reported || _consecutiveSymbols < RequiredConsecutiveSymbols) return;
        _reported = true;
        if (_metrics is not null) _metrics.PreambleDetections++;
        PreambleDetected?.Invoke(new LoRaPreambleDetection(
            DateTimeOffset.UtcNow,
            peak.PeakToAverageDb,
            peak.FrequencyHz - _expectedCenterOffsetHz,
            _consecutiveSymbols)
        {
            EstimatedSnrDb = _snrEstimator.EstimatedSnrDb,
            EstimatedRssiDbfs = _snrEstimator.EstimatedRssiDbfs
        });

        // A cyclic time shift of an up-chirp appears as a dechirped tone. Preserve
        // the interpolated FFT position here: at 500 kS/s one chip is two samples,
        // so an integer-bin correction can otherwise select only one CIC phase.
        float timingSymbol = Mod(ToSignedBin(peak.InterpolatedBin) - _expectedCenterBin, _symbolValues);
        int timingOffsetSamples = Mod(
            (int)MathF.Round(timingSymbol * _samplesPerChip, MidpointRounding.AwayFromZero),
            _symbolSamples);
        ReportDiagnostic(
            "TIMING",
            $"Fine boundary correction scheduled: symbolOffset={timingSymbol:F3} sampleOffset={timingOffsetSamples}",
            peak,
            (int)MathF.Round(timingSymbol),
            null,
            false);
        _samplesSinceAnalysis = timingOffsetSamples;
        _state = AcquisitionState.Aligning;
    }

    private void ProcessAlignedPreamble(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < UpChirpThresholdDb)
        {
            ReportDiagnostic("ALIGN", "Aligned preamble window did not contain a usable up-chirp", peak, null, null, true);
            ResetAcquisition();
            return;
        }

        float alignedFrequencyHz = peak.FrequencyHz - _expectedCenterOffsetHz;
        float maximumResidualHz = _bandwidthHz * 0.45f;
        if (Math.Abs(alignedFrequencyHz) > maximumResidualHz)
        {
            ReportDiagnostic(
                "ALIGN",
                $"Aligned preamble is outside the selected channel: residual={alignedFrequencyHz:F1}Hz limit={maximumResidualHz:F1}Hz",
                peak,
                null,
                null,
                true);
            ResetAcquisition();
            return;
        }

        _preamblePeakBin = peak.InterpolatedBin;
        _snrEstimator.BreakSequence();
        _snrEstimator.Observe(_ring.AsSpan(_writeIndex, _symbolSamples));
        _lastPeakBin = peak.Bin;
        _missedSymbols = 0;
        _state = AcquisitionState.Preamble;
        ReportDiagnostic(
            "ALIGN",
            $"Preamble boundary aligned: referenceBin={ToSignedBin(peak.InterpolatedBin):F3}",
            peak,
            0,
            0,
            false);
    }

    private void ProcessPreambleOrSyncHigh(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < UpChirpThresholdDb)
        {
            _snrEstimator.BreakSequence();
            RegisterFrameMiss("SYNC1", "Up-chirp margin fell below threshold while waiting for sync word", peak, null, expectedSymbol: 16);
            return;
        }

        float delta = SymbolDelta(peak.InterpolatedBin, _preamblePeakBin);
        if (Math.Abs(delta) <= PeakBinTolerance)
        {
            _snrEstimator.Observe(_ring.AsSpan(_writeIndex, _symbolSamples));
            _missedSymbols = 0;
            return;
        }

        int expectedHigh = DecodeSyncNibble((_syncWord >> 4) & 0x0F);
        if (Math.Abs(delta - expectedHigh) <= PeakBinTolerance ||
            Math.Abs(delta + expectedHigh) <= PeakBinTolerance)
        {
            _syncDirection = Math.Abs(delta - expectedHigh) <= PeakBinTolerance ? 1 : -1;
            _missedSymbols = 0;
            _state = AcquisitionState.SyncLow;
            ReportDiagnostic("SYNC1", "First sync-word symbol accepted", peak, (int)MathF.Round(delta), _syncDirection * expectedHigh, false);
            return;
        }

        int observedDelta = (int)MathF.Round(delta);
        _snrEstimator.BreakSequence();
        ReportDiagnostic("SYNC1", "Candidate did not match first sync-word symbol", peak, observedDelta, expectedHigh, true);
        RegisterFrameMiss("SYNC1", "Repeated first sync-word mismatch; acquisition reset", peak, observedDelta, expectedHigh);
    }

    private void ProcessSyncLow(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < UpChirpThresholdDb)
        {
            ReportDiagnostic("SYNC2", "Second sync-word symbol had insufficient margin", peak, null, null, true);
            ResetAcquisition();
            return;
        }

        int expectedLow = DecodeSyncNibble(_syncWord & 0x0F);
        float delta = SymbolDelta(peak.InterpolatedBin, _preamblePeakBin);
        if (Math.Abs(delta - (_syncDirection * expectedLow)) > PeakBinTolerance)
        {
            ReportDiagnostic("SYNC2", "Second sync-word symbol rejected", peak, (int)MathF.Round(delta), _syncDirection * expectedLow, true);
            ResetAcquisition();
            return;
        }

        _sfdSymbols = 0;
        _sfdDownPeakHzSum = 0;
        _state = AcquisitionState.Sfd;
        ReportDiagnostic("SYNC2", "Second sync-word symbol accepted", peak, (int)MathF.Round(delta), _syncDirection * expectedLow, false);
    }

    private void ProcessSfd(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < DownChirpThresholdDb)
        {
            ReportDiagnostic("SFD", $"Down-chirp {_sfdSymbols + 1} had insufficient margin", peak, null, null, true);
            ResetAcquisition();
            return;
        }

        _sfdSymbols++;
        _sfdDownPeakHzSum += peak.FrequencyHz;
        _lastDownPeakHz = _sfdDownPeakHzSum / _sfdSymbols;
        ReportDiagnostic("SFD", $"Down-chirp {_sfdSymbols} accepted", peak, null, null, false);
        if (_sfdSymbols < 2) return;

        float upChirpPeakHz = BinToFrequencyHz(_preamblePeakBin) - _expectedCenterOffsetHz;
        float downChirpPeakHz = _lastDownPeakHz - _expectedCenterOffsetHz;
        float carrierFrequencyOffsetHz = (upChirpPeakHz + downChirpPeakHz) * 0.5f;
        float timingToneHz = (upChirpPeakHz - downChirpPeakHz) * 0.5f;
        int requestedTimingCorrectionSamples = (int)MathF.Round(
            timingToneHz * _symbolSamples / _bandwidthHz,
            MidpointRounding.AwayFromZero);
        bool compensationRequired =
            float.IsFinite(carrierFrequencyOffsetHz) &&
            Math.Abs(carrierFrequencyOffsetHz) >= _bandwidthHz * 0.02f;
        bool compensationApplied =
            compensationRequired &&
            float.IsFinite(carrierFrequencyOffsetHz) &&
            Math.Abs(carrierFrequencyOffsetHz) <= _bandwidthHz * 0.20f &&
            Math.Abs(requestedTimingCorrectionSamples) <= _symbolSamples / 4;
        int timingCorrectionSamples = compensationApplied ? requestedTimingCorrectionSamples : 0;
        _frameTimingCorrectionSamples = timingCorrectionSamples;
        if (compensationApplied)
        {
            // Coarse acquisition cannot distinguish carrier offset from a cyclic
            // chirp time shift. The opposite SFD slope separates the two. Move
            // the header window to the true boundary, derotate subsequent IQ,
            // and return the relative-symbol reference to the selected channel.
            _carrierFrequencyCorrectionHz = carrierFrequencyOffsetHz;
            _preamblePeakBin = FrequencyToShiftedBin(_expectedCenterOffsetHz);
        }

        if (_metrics is not null) _metrics.FrameSynchronizations++;
        FrameSynchronized?.Invoke(new LoRaFrameSynchronization(
            DateTimeOffset.UtcNow,
            _syncWord,
            upChirpPeakHz,
            downChirpPeakHz,
            _symbolSamples / 4,
            carrierFrequencyOffsetHz,
            timingCorrectionSamples,
            compensationApplied,
            compensationRequired)
        {
            EstimatedSnrDb = _snrEstimator.EstimatedSnrDb,
            EstimatedRssiDbfs = _snrEstimator.EstimatedRssiDbfs
        });
        ReportDiagnostic(
            "CFO",
            compensationApplied
                ? $"Frame compensation applied: carrierOffset={carrierFrequencyOffsetHz:F1}Hz timingCorrection={timingCorrectionSamples} samples"
                : compensationRequired
                    ? $"Frame compensation skipped: carrierOffset={carrierFrequencyOffsetHz:F1}Hz timingCorrection={requestedTimingCorrectionSamples} samples"
                    : $"Frame compensation not required: carrierOffset={carrierFrequencyOffsetHz:F1}Hz",
            peak,
            timingCorrectionSamples,
            null,
            false);
        _frameDecoder.BeginHeader();
        _consecutiveLowSnrSymbols = 0;
        _samplesSinceAnalysis = -(_symbolSamples / 4) + timingCorrectionSamples;
        _state = AcquisitionState.Header;
    }

    private void ProcessHeader(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < 2.0f)
        {
            _consecutiveLowSnrSymbols++;
            if (_consecutiveLowSnrSymbols >= 4)
            {
                ReportDiagnostic("HEADER", $"Header symbol {_frameDecoder.HeaderSymbolCount + 1} had persistently insufficient margin", peak, null, null, true);
                ResetAcquisition();
                return;
            }
        }
        else
        {
            _consecutiveLowSnrSymbols = 0;
        }

        float relativeRawSymbol = Mod(
            ToSignedBin(peak.InterpolatedBin) - ToSignedBin(_preamblePeakBin),
            _symbolValues);
        int reducedRateSymbol = Mod(
            (int)MathF.Round((relativeRawSymbol - 1.0f) / 4.0f, MidpointRounding.AwayFromZero),
            _symbolValues / 4);
        int grayMappedSymbol = reducedRateSymbol ^ (reducedRateSymbol >> 1);
        LoRaExplicitHeader? header = _frameDecoder.PushHeader((ushort)grayMappedSymbol,
            _softOptions is not null ? CreateSoftSymbol(peak, reducedRate: true) : null);
        if (header is null) return;
        if (_metrics is not null)
        {
            _metrics.HeadersDecoded++;
            if (header.IsChecksumValid) _metrics.HeaderChecksumsValid++;
        }
        ExplicitHeaderDecoded?.Invoke(header);
        if (!header.IsChecksumValid)
        {
            ReportDiagnostic(
                "HEADER",
                $"Header checksum rejected: received=0x{header.HeaderChecksum:X2} calculated=0x{header.CalculatedChecksum:X2}",
                peak,
                header.HeaderChecksum,
                header.CalculatedChecksum,
                true);
            ResetAcquisition();
            return;
        }

        ReportDiagnostic(
            "HEADER",
            $"Header accepted: length={header.PayloadLength} CR=4/{header.CodingRateDenominator} payloadCrc={header.HasPayloadCrc}",
            peak,
            null,
            null,
            false);

        _frameDecoder.BeginPayload(header);
        _consecutiveLowSnrSymbols = 0;
        if (!TryCompletePayload()) _state = AcquisitionState.Payload;
    }

    private void ProcessPayload(SpectrumPeak peak)
    {
        LoRaExplicitHeader? header = _frameDecoder.ActiveHeader;
        if (header == null || !_frameDecoder.HasPayload)
        {
            ReportDiagnostic("PAYLOAD", $"Payload symbol {_frameDecoder.PayloadBlockSymbolCount + 1} was unusable", peak, null, null, true);
            ResetAcquisition();
            return;
        }

        if (peak.PeakToAverageDb < 2.0f)
        {
            _consecutiveLowSnrSymbols++;
            if (_consecutiveLowSnrSymbols >= 4)
            {
                ReportDiagnostic("PAYLOAD", $"Payload symbol {_frameDecoder.PayloadBlockSymbolCount + 1} had persistently unusable margin", peak, null, null, true);
                ResetAcquisition();
                return;
            }
        }
        else
        {
            _consecutiveLowSnrSymbols = 0;
        }

        float relativeRawSymbol = Mod(
            ToSignedBin(peak.InterpolatedBin) - ToSignedBin(_preamblePeakBin),
            _symbolValues);
        int binarySymbol = Mod(
            (int)MathF.Round(relativeRawSymbol - 1.0f, MidpointRounding.AwayFromZero),
            _symbolValues);
        int grayMappedSymbol = binarySymbol ^ (binarySymbol >> 1);
        if (_frameDecoder.PushPayload((ushort)grayMappedSymbol,
            _softOptions is not null ? CreateSoftSymbol(peak, reducedRate: false) : null))
            TryCompletePayload();
    }

    private bool TryCompletePayload()
    {
        LoRaPayloadFrame? frame = _frameDecoder.BuildCompletedPayload(out LoRaCrcListDecodingResult? listResult);
        if (frame is null) return false;
        if (listResult is not null)
        {
            if (_metrics is not null)
            {
                _metrics.CrcListEligibleFrames++;
                _metrics.CrcListRetries += listResult.Retries;
                _metrics.CrcListTicks += listResult.ElapsedTicks;
                if (listResult.Recovered) _metrics.CrcListRecoveredFrames++;
                if (listResult.Ambiguous) _metrics.CrcListAmbiguousFrames++;
            }
            CrcListDecodingCompleted?.Invoke(new LoRaCrcListDecodingDiagnostic(
                DateTimeOffset.UtcNow,
                listResult.Retries,
                listResult.MatchingCandidates,
                listResult.UniqueMatchingFrames,
                listResult.Recovered,
                listResult.Ambiguous,
                listResult.TimeLimitReached,
                listResult.SelectedMetric,
                listResult.ElapsedTicks));
        }
        if (_metrics is not null)
        {
            _metrics.PayloadsDecoded++;
            if (frame.IsPayloadCrcValid is true) _metrics.PayloadCrcValid++;
            else if (frame.IsPayloadCrcValid is false) _metrics.PayloadCrcInvalid++;
        }
        frame = frame with
        {
            EstimatedSnrDb = _snrEstimator.EstimatedSnrDb,
            EstimatedRssiDbfs = _snrEstimator.EstimatedRssiDbfs
        };
        PayloadDecoded?.Invoke(frame);
        ResetAcquisition();
        return true;
    }
}
