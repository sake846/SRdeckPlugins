using System;
using System.Collections.Generic;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

internal sealed partial class LoRaPreambleDetector
{
    private void ProcessSearch(SpectrumPeak peak)
    {
        if (peak.PeakToAverageDb < UpChirpThresholdDb)
        {
            RegisterSearchMiss();
            return;
        }

        if (_lastPeakBin >= 0 && CircularBinDistance(peak.Bin, _lastPeakBin, _symbolSamples) <= PeakBinTolerance)
        {
            _consecutiveSymbols++;
        }
        else
        {
            _consecutiveSymbols = 1;
        }
        _lastPeakBin = peak.Bin;
        _missedSymbols = 0;

        if (_reported || _consecutiveSymbols < RequiredConsecutiveSymbols) return;
        _reported = true;
        PreambleDetected?.Invoke(new LoRaPreambleDetection(
            DateTimeOffset.UtcNow,
            peak.PeakToAverageDb,
            peak.FrequencyHz - _expectedCenterOffsetHz,
            _consecutiveSymbols));

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
            RegisterFrameMiss("SYNC1", "Up-chirp margin fell below threshold while waiting for sync word", peak, null, expectedSymbol: 16);
            return;
        }

        float delta = SymbolDelta(peak.InterpolatedBin, _preamblePeakBin);
        if (Math.Abs(delta) <= PeakBinTolerance)
        {
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
        if (compensationApplied)
        {
            // Coarse acquisition cannot distinguish carrier offset from a cyclic
            // chirp time shift. The opposite SFD slope separates the two. Move
            // the header window to the true boundary, derotate subsequent IQ,
            // and return the relative-symbol reference to the selected channel.
            _carrierFrequencyCorrectionHz = carrierFrequencyOffsetHz;
            _preamblePeakBin = FrequencyToShiftedBin(_expectedCenterOffsetHz);
        }

        FrameSynchronized?.Invoke(new LoRaFrameSynchronization(
            DateTimeOffset.UtcNow,
            _syncWord,
            upChirpPeakHz,
            downChirpPeakHz,
            _symbolSamples / 4,
            carrierFrequencyOffsetHz,
            timingCorrectionSamples,
            compensationApplied,
            compensationRequired));
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
        _headerSymbolCount = 0;
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
                ReportDiagnostic("HEADER", $"Header symbol {_headerSymbolCount + 1} had persistently insufficient margin", peak, null, null, true);
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
        _headerSymbols[_headerSymbolCount++] = (ushort)grayMappedSymbol;
        if (_headerSymbolCount < _headerSymbols.Length) return;

        LoRaExplicitHeader header = LoRaExplicitHeaderDecoder.Decode(_headerSymbols, _spreadingFactor);
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

        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        _activeHeader = header;
        _payloadNibbles = new List<byte>(requiredNibbles);
        foreach (byte nibble in header.InitialPayloadNibbles)
        {
            if (_payloadNibbles.Count == requiredNibbles) break;
            _payloadNibbles.Add(nibble);
        }
        _payloadCorrectedCodewords = header.CorrectedCodewords;
        _payloadBlockSymbolCount = 0;
        _consecutiveLowSnrSymbols = 0;
        if (!TryCompletePayload()) _state = AcquisitionState.Payload;
    }

    private void ProcessPayload(SpectrumPeak peak)
    {
        LoRaExplicitHeader? header = _activeHeader;
        if (header == null || _payloadNibbles == null)
        {
            ReportDiagnostic("PAYLOAD", $"Payload symbol {_payloadBlockSymbolCount + 1} was unusable", peak, null, null, true);
            ResetAcquisition();
            return;
        }

        if (peak.PeakToAverageDb < 2.0f)
        {
            _consecutiveLowSnrSymbols++;
            if (_consecutiveLowSnrSymbols >= 4)
            {
                ReportDiagnostic("PAYLOAD", $"Payload symbol {_payloadBlockSymbolCount + 1} had persistently unusable margin", peak, null, null, true);
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
        _payloadBlockSymbols[_payloadBlockSymbolCount++] = (ushort)grayMappedSymbol;
        if (_payloadBlockSymbolCount < header.CodingRateDenominator) return;

        byte[] nibbles = LoRaPayloadDecoder.DecodeBlock(
            _payloadBlockSymbols.AsSpan(0, header.CodingRateDenominator).ToArray(),
            header.CodingRateDenominator,
            _spreadingFactor,
            out int correctedCodewords);
        _payloadCorrectedCodewords += correctedCodewords;
        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        foreach (byte nibble in nibbles)
        {
            if (_payloadNibbles.Count == requiredNibbles) break;
            _payloadNibbles.Add(nibble);
        }
        _payloadBlockSymbolCount = 0;
        TryCompletePayload();
    }

    private bool TryCompletePayload()
    {
        LoRaExplicitHeader? header = _activeHeader;
        List<byte>? nibbles = _payloadNibbles;
        if (header == null || nibbles == null) return false;

        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        if (nibbles.Count < requiredNibbles) return false;

        LoRaPayloadFrame frame = LoRaPayloadDecoder.BuildFrame(header, nibbles, _payloadCorrectedCodewords);
        PayloadDecoded?.Invoke(frame);
        ResetAcquisition();
        return true;
    }
}
