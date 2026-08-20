using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    private static bool TryReadHeader(IReadOnlyList<bool> rawBits, out int dataBits,
        out int dataOctets, out int fecOctets, out HeaderDecodeStatus status)
    {
        bool[] headerBits = rawBits.Take(HeaderLength).ToArray();
        Descramble(headerBits, HeaderLength);
        uint header = ReadMsbWord(headerBits, 0, HeaderLength);
        int syndrome = HeaderSyndrome(header);
        status = syndrome == 0 ? HeaderDecodeStatus.Clean : HeaderDecodeStatus.Corrected;
        uint correction = HeaderCorrections[syndrome];
        if (syndrome != 0 && correction == 0)
        {
            dataBits = dataOctets = fecOctets = 0;
            status = HeaderDecodeStatus.FecUncorrectable;
            return false;
        }
        header ^= correction;
        if (HeaderSyndrome(header) != 0)
        {
            dataBits = dataOctets = fecOctets = 0;
            status = HeaderDecodeStatus.FecUncorrectable;
            return false;
        }
        uint encodedLength = (header >> HeaderFecBits) & ((1u << TransmissionLengthBits) - 1);
        dataBits = (int)ReverseBits(encodedLength, TransmissionLengthBits);
        if (dataBits <= 0 || dataBits > MaxTransmissionBits)
        {
            dataOctets = fecOctets = 0;
            status = HeaderDecodeStatus.InvalidLength;
            return false;
        }
        dataOctets = (dataBits + 7) / 8;
        int fullBlocks = dataOctets / ReedSolomonDataBytes;
        int finalBlockBytes = dataOctets % ReedSolomonDataBytes;
        fecOctets = fullBlocks * (ReedSolomonCodewordBytes - ReedSolomonDataBytes);
        if (finalBlockBytes != 0) fecOctets += GetFecOctetCount(finalBlockBytes);
        if (fecOctets > 0) return true;
        status = HeaderDecodeStatus.InvalidLength;
        return false;
    }

    private void DecodeBurst(IqBlockMetadata metadata, List<VdlFrame> output) =>
        DecodeBurst(metadata, output, CreateRecoveryDeadline());

    private void DecodeBurst(IqBlockMetadata metadata, List<VdlFrame> output,
        long recoveryDeadline)
    {
        if (BurstQualityObserver is not null)
        {
            int bitCount = Math.Min(requestedBurstBits, burstReliabilities.Count);
            double averageReliability = bitCount == 0 ? 0 : burstReliabilities.Take(bitCount).Average();
            double carrierRms = burstCarrierUpdateCount == 0 ? 0 :
                Math.Sqrt(burstCarrierErrorPower / burstCarrierUpdateCount);
            double timingRms = burstTimingUpdateCount == 0 ? 0 :
                Math.Sqrt(burstTimingErrorPower / burstTimingUpdateCount);
            BurstQualityObserver(new(burstCarrierUpdateCount, averageReliability, carrierRms,
                timingRms, lastTimingOffsetSamples, timingRateCorrection));
        }
        bool[] clearBits = burstBits.Take(requestedBurstBits).ToArray();
        Descramble(clearBits, clearBits.Length);
        int dataOctets = (transmissionLength + 7) / 8;
        int blockCount = (dataOctets + ReedSolomonDataBytes - 1) / ReedSolomonDataBytes;
        int lastBlockLength = dataOctets % ReedSolomonDataBytes;
        if (lastBlockLength == 0) lastBlockLength = ReedSolomonDataBytes;
        // The header is not byte-aligned, so derive parity from the RS block layout.
        int fecOctets = (blockCount - 1) * (ReedSolomonCodewordBytes - ReedSolomonDataBytes) +
                        GetFecOctetCount(lastBlockLength);
        byte[] transmittedData = PackLsb(clearBits, HeaderLength, dataOctets);
        byte[] transmittedFec = PackLsb(clearBits, HeaderLength + dataOctets * 8, fecOctets);
        double[] transmittedDataReliability = PackReliability(
            burstReliabilities, HeaderLength, dataOctets);
        double[] transmittedFecReliability = PackReliability(
            burstReliabilities, HeaderLength + dataOctets * 8, fecOctets);
        var blocks = new byte[blockCount][];
        var blockReliabilities = new double[blockCount][];
        for (int row = 0; row < blockCount; row++)
        {
            blocks[row] = new byte[ReedSolomonCodewordBytes];
            blockReliabilities[row] = Enumerable.Repeat(double.PositiveInfinity,
                ReedSolomonCodewordBytes).ToArray();
        }
        if (!TryDeinterleave(transmittedData, blockCount, ReedSolomonDataBytes, 0, blocks))
        {
            RejectedFrameCount++;
            return;
        }
        if (!TryDeinterleave(transmittedDataReliability, blockCount,
                ReedSolomonDataBytes, 0, blockReliabilities))
        {
            RejectedFrameCount++;
            return;
        }
        int fecRows = blockCount - (GetFecOctetCount(lastBlockLength) == 0 ? 1 : 0);
        if (fecRows > 0 && !TryDeinterleave(transmittedFec, fecRows,
                ReedSolomonCodewordBytes - ReedSolomonDataBytes, ReedSolomonDataBytes, blocks))
        {
            RejectedFrameCount++;
            return;
        }
        if (fecRows > 0 && !TryDeinterleave(transmittedFecReliability, fecRows,
                ReedSolomonCodewordBytes - ReedSolomonDataBytes,
                ReedSolomonDataBytes, blockReliabilities))
        {
            RejectedFrameCount++;
            return;
        }

        var data = new byte[dataOctets];
        int destination = 0;
        int softRecoveredBlocks = 0;
        int softRecoveredOctets = 0;
        for (int row = 0; row < blockCount; row++)
        {
            int blockDataLength = row == blockCount - 1 ? lastBlockLength : ReedSolomonDataBytes;
            int blockFecLength = row == blockCount - 1 ? GetFecOctetCount(lastBlockLength) :
                ReedSolomonCodewordBytes - ReedSolomonDataBytes;
            byte[] receivedBlock = (byte[])blocks[row].Clone();
            bool softRecovered = false;
            if (!VdlReedSolomon.TryDecode(blocks[row], blockDataLength, blockFecLength,
                    out int correctedSymbols))
            {
                FecSoftAttemptBlockCount++;
                Array.Copy(receivedBlock, blocks[row], receivedBlock.Length);
                int[] leastReliable = Enumerable.Range(0, blockDataLength)
                    .Concat(Enumerable.Range(ReedSolomonDataBytes, blockFecLength))
                    .OrderBy(position => blockReliabilities[row][position])
                    .Take(blockFecLength)
                    .ToArray();
                for (int erasureCount = 1; erasureCount <= leastReliable.Length; erasureCount++)
                {
                    Array.Copy(receivedBlock, blocks[row], receivedBlock.Length);
                    if (!VdlReedSolomon.TryDecodeWithErasures(blocks[row], blockDataLength,
                            blockFecLength, leastReliable.AsSpan(0, erasureCount),
                            out correctedSymbols)) continue;
                    softRecovered = true;
                    break;
                }
                if (!softRecovered)
                {
                    FecUncorrectableBlockCount++;
                    RejectedFrameCount++;
                    return;
                }
            }
            if (blockFecLength == 0) FecUnprotectedBlockCount++;
            else if (softRecovered)
            {
                softRecoveredBlocks++;
                softRecoveredOctets += correctedSymbols;
            }
            else if (correctedSymbols == 0) FecCleanBlockCount++;
            else
            {
                FecCorrectedBlockCount++;
                FecCorrectedOctetCount += correctedSymbols;
            }
            Array.Copy(blocks[row], 0, data, destination, blockDataLength);
            destination += blockDataLength;
        }
        var dataBits = new List<bool>(transmissionLength);
        foreach (byte value in data)
            for (int bit = 0; bit < 8 && dataBits.Count < transmissionLength; bit++)
                dataBits.Add((value & (1 << bit)) != 0);
        DecodedBurstObserver?.Invoke((byte[])data.Clone(), softRecoveredBlocks > 0);

        int acceptedBefore = output.Count;
        ExtractAvlcFrames(dataBits, metadata, targetFrequencyHz, output);
        int accepted = output.Count - acceptedBefore;
        ValidFrameCount += accepted;
        if (accepted == 0)
        {
            FecSoftRejectedBlockCount += softRecoveredBlocks;
            RejectedFrameCount++;
            bool recoveryBudgetExceeded = false;
            if (rescueDecodingEnabled && TryDecodeChase(clearBits, metadata, recoveryDeadline,
                    out IReadOnlyList<VdlFrame>? chaseFrames, out recoveryBudgetExceeded))
            {
                output.AddRange(chaseFrames);
                ValidFrameCount += chaseFrames.Count;
                RejectedFrameCount = Math.Max(0, RejectedFrameCount - 1);
                ChaseSuccessCount++;
                ChaseRecoveredFrameCount += chaseFrames.Count;
            }
            else if (rescueDecodingEnabled && !recoveryBudgetExceeded && phaseHypothesesEnabled &&
                     TryDecodePhaseHypotheses(metadata, recoveryDeadline,
                         out IReadOnlyList<VdlFrame>? recoveredFrames, out recoveryBudgetExceeded))
            {
                output.AddRange(recoveredFrames);
                ValidFrameCount += recoveredFrames.Count;
                RejectedFrameCount = Math.Max(0, RejectedFrameCount - 1);
                PhaseHypothesisSuccessCount++;
                PhaseHypothesisRecoveredFrameCount += recoveredFrames.Count;
            }
            if (recoveryBudgetExceeded) RecoveryBudgetExceededCount++;
        }
        else
        {
            FecSoftCorrectedBlockCount += softRecoveredBlocks;
            FecSoftCorrectedOctetCount += softRecoveredOctets;
        }
    }

    private bool TryDecodePhaseHypotheses(IqBlockMetadata metadata, long recoveryDeadline,
        out IReadOnlyList<VdlFrame> recoveredFrames, out bool recoveryBudgetExceeded)
    {
        recoveredFrames = [];
        recoveryBudgetExceeded = false;
        if (burstSymbols.Count == 0) return false;
        (IReadOnlyList<Complex32> Symbols, double TimingOffset)[] timingCandidates =
        [
            (burstSymbolsEarly, -0.25),
            (burstSymbols, 0),
            (burstSymbolsLate, 0.25)
        ];
        double[] frequencyOffsetsHz = [-400, -300, -200, -100, -50, 0, 50, 100, 200, 300, 400];
        foreach ((IReadOnlyList<Complex32> symbols, double timingOffset) in timingCandidates)
        foreach (double frequencyOffsetHz in frequencyOffsetsHz)
        {
            if (IsRecoveryDeadlineExceeded(recoveryDeadline))
            {
                recoveryBudgetExceeded = true;
                return false;
            }
            if (timingOffset == 0 && frequencyOffsetHz == 0) continue;
            PhaseHypothesisAttemptCount++;
            DemodulateHypothesis(symbols, frequencyOffsetHz,
                out List<bool> candidateBits, out List<double> candidateReliabilities);
            if (candidateBits.Count < requestedBurstBits ||
                !TryReadHeader(candidateBits, out int candidateLength, out _, out _, out _) ||
                candidateLength != transmissionLength) continue;

            var candidate = new VdlMode2Receiver
            {
                phaseHypothesesEnabled = false,
                rescueDecodingEnabled = false,
                targetFrequencyHz = targetFrequencyHz,
                transmissionLength = transmissionLength,
                requestedBurstBits = requestedBurstBits,
                CarrierTrackingLoopGain = CarrierTrackingLoopGain,
                TimingRecoveryEnabled = TimingRecoveryEnabled,
                TimingRecoveryLoopGainScale = TimingRecoveryLoopGainScale
            };
            candidate.burstBits.AddRange(candidateBits.Take(requestedBurstBits));
            candidate.burstReliabilities.AddRange(
                candidateReliabilities.Take(requestedBurstBits));
            var frames = new List<VdlFrame>();
            candidate.DecodeBurst(metadata, frames, recoveryDeadline);
            if (IsRecoveryDeadlineExceeded(recoveryDeadline))
            {
                recoveryBudgetExceeded = true;
                return false;
            }
            if (frames.Count == 0) continue;
            lastPhaseHypothesisTimingOffset = timingOffset;
            lastPhaseHypothesisFrequencyOffsetHz = frequencyOffsetHz;
            recoveredFrames = frames;
            return true;
        }
        return false;
    }

    private bool TryDecodeChase(IReadOnlyList<bool> clearBits, IqBlockMetadata metadata,
        long recoveryDeadline, out IReadOnlyList<VdlFrame> recoveredFrames,
        out bool recoveryBudgetExceeded)
    {
        recoveredFrames = [];
        recoveryBudgetExceeded = false;
        if (IsRecoveryDeadlineExceeded(recoveryDeadline))
        {
            recoveryBudgetExceeded = true;
            return false;
        }
        int dataOctets = (transmissionLength + 7) / 8;
        int blockCount = (dataOctets + ReedSolomonDataBytes - 1) / ReedSolomonDataBytes;
        int lastBlockLength = dataOctets % ReedSolomonDataBytes;
        if (lastBlockLength == 0) lastBlockLength = ReedSolomonDataBytes;
        int fecOctets = (blockCount - 1) * (ReedSolomonCodewordBytes - ReedSolomonDataBytes) +
                        GetFecOctetCount(lastBlockLength);
        byte[] transmittedData = PackLsb(clearBits, HeaderLength, dataOctets);
        byte[] transmittedFec = PackLsb(clearBits, HeaderLength + dataOctets * 8, fecOctets);
        double[] dataReliability = PackReliability(burstReliabilities, HeaderLength, dataOctets);
        double[] fecReliability = PackReliability(burstReliabilities,
            HeaderLength + dataOctets * 8, fecOctets);
        var blocks = new byte[blockCount][];
        var reliability = new double[blockCount][];
        for (int row = 0; row < blockCount; row++)
        {
            blocks[row] = new byte[ReedSolomonCodewordBytes];
            reliability[row] = Enumerable.Repeat(double.PositiveInfinity,
                ReedSolomonCodewordBytes).ToArray();
        }
        if (!TryDeinterleave(transmittedData, blockCount, ReedSolomonDataBytes, 0, blocks) ||
            !TryDeinterleave(dataReliability, blockCount, ReedSolomonDataBytes, 0, reliability))
            return false;
        int fecRows = blockCount - (GetFecOctetCount(lastBlockLength) == 0 ? 1 : 0);
        if (fecRows > 0 &&
            (!TryDeinterleave(transmittedFec, fecRows, ReedSolomonCodewordBytes -
                    ReedSolomonDataBytes, ReedSolomonDataBytes, blocks) ||
             !TryDeinterleave(fecReliability, fecRows, ReedSolomonCodewordBytes -
                    ReedSolomonDataBytes, ReedSolomonDataBytes, reliability))) return false;

        var blockCandidates = new List<byte[]>[blockCount];
        for (int row = 0; row < blockCount; row++)
        {
            if (IsRecoveryDeadlineExceeded(recoveryDeadline))
            {
                recoveryBudgetExceeded = true;
                return false;
            }
            int dataLength = row == blockCount - 1 ? lastBlockLength : ReedSolomonDataBytes;
            int fecLength = row == blockCount - 1 ? GetFecOctetCount(lastBlockLength) :
                ReedSolomonCodewordBytes - ReedSolomonDataBytes;
            blockCandidates[row] = BuildChaseBlockCandidates(
                blocks[row], reliability[row], dataLength, fecLength, recoveryDeadline,
                out bool candidateBudgetExceeded);
            if (candidateBudgetExceeded)
            {
                recoveryBudgetExceeded = true;
                return false;
            }
            if (blockCandidates[row].Count == 0) return false;
        }

        var assembled = new List<byte[]> { new byte[dataOctets] };
        int destination = 0;
        for (int row = 0; row < blockCount; row++)
        {
            int dataLength = row == blockCount - 1 ? lastBlockLength : ReedSolomonDataBytes;
            var next = new List<byte[]>();
            foreach (byte[] prefix in assembled)
            {
                foreach (byte[] candidate in blockCandidates[row])
                {
                    if (IsRecoveryDeadlineExceeded(recoveryDeadline))
                    {
                        recoveryBudgetExceeded = true;
                        return false;
                    }
                    byte[] combined = (byte[])prefix.Clone();
                    Array.Copy(candidate, 0, combined, destination, dataLength);
                    next.Add(combined);
                    if (next.Count >= 512) break;
                }
                if (next.Count >= 512) break;
            }
            assembled = next;
            destination += dataLength;
        }

        foreach (byte[] data in assembled)
        {
            if (IsRecoveryDeadlineExceeded(recoveryDeadline))
            {
                recoveryBudgetExceeded = true;
                return false;
            }
            ChaseAttemptCount++;
            var dataBits = new List<bool>(transmissionLength);
            foreach (byte value in data)
                for (int bit = 0; bit < 8 && dataBits.Count < transmissionLength; bit++)
                    dataBits.Add((value & (1 << bit)) != 0);
            var frames = new List<VdlFrame>();
            ExtractAvlcFrames(dataBits, metadata, targetFrequencyHz, frames,
                countDiagnostics: false);
            if (frames.Count == 0) continue;
            recoveredFrames = frames;
            return true;
        }
        return false;
    }

    private static List<byte[]> BuildChaseBlockCandidates(byte[] received,
        double[] reliability, int dataLength, int fecLength, long recoveryDeadline,
        out bool recoveryBudgetExceeded)
    {
        recoveryBudgetExceeded = false;
        byte[] hard = (byte[])received.Clone();
        if (VdlReedSolomon.TryDecode(hard, dataLength, fecLength, out _)) return [hard];
        if (fecLength == 0) return [(byte[])received.Clone()];

        int[] positions = Enumerable.Range(0, dataLength)
            .Concat(Enumerable.Range(ReedSolomonDataBytes, fecLength))
            .OrderBy(position => reliability[position])
            .Take(Math.Min(dataLength + fecLength, fecLength + 4))
            .ToArray();
        var results = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int erasures = 1; erasures <= fecLength; erasures++)
        foreach (int[] combination in EnumerateCombinations(positions, erasures))
        {
            if (IsRecoveryDeadlineExceeded(recoveryDeadline))
            {
                recoveryBudgetExceeded = true;
                return [];
            }
            byte[] candidate = (byte[])received.Clone();
            if (!VdlReedSolomon.TryDecodeWithErasures(candidate, dataLength, fecLength,
                    combination, out _)) continue;
            string key = Convert.ToHexString(candidate.AsSpan(0, dataLength));
            if (seen.Add(key)) results.Add(candidate);
            if (results.Count >= 128) return results;
        }
        return results;
    }
}
