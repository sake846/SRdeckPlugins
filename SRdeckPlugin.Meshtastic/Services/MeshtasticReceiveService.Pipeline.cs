using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Contracts;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Services;

public sealed partial class MeshtasticReceiveService
{
    private void HandlePreambleDetected(ChannelConfiguration _, DetectorReceptionState state, LoRaPreambleDetection detection)
    {
        state.LastDetection = detection;
        _lastDetection = detection;
        Interlocked.Increment(ref _detectedPreambles);
        PreambleDetected?.Invoke(detection);
    }

    private void HandleFrameSynchronized(ChannelConfiguration _, DetectorReceptionState state, LoRaFrameSynchronization synchronization)
    {
        state.LastSynchronization = synchronization;
        _lastSynchronization = synchronization;
        Interlocked.Increment(ref _synchronizedFrames);
        FrameSynchronized?.Invoke(synchronization);
    }

    private void HandleExplicitHeaderDecoded(ChannelConfiguration _, DetectorReceptionState state, LoRaExplicitHeader header)
    {
        state.LastHeader = header;
        _lastHeader = header;
        Interlocked.Increment(ref _decodedHeaders);
        ExplicitHeaderDecoded?.Invoke(header);
    }

    private void HandlePayloadDecoded(ChannelConfiguration channel, DetectorReceptionState state, LoRaPayloadFrame frame)
    {
        _lastPayload = frame;
        Interlocked.Increment(ref _decodedPayloads);
        bool accepted = frame.IsPayloadCrcValid is not false;
        PayloadDecoded?.Invoke(frame);

        if (!accepted || frame.Payload.Length < MeshtasticRadioPacketParser.HeaderLength) return;
        var quality = new MeshtasticLoRaReceptionQuality(
            frame.EstimatedSnrDb,
            state.LastDetection?.DechirpedPeakHz,
            state.LastSynchronization?.UpChirpPeakHz,
            state.LastSynchronization?.DownChirpPeakHz,
            frame.IsPayloadCrcValid,
            frame.CorrectedCodewords)
        {
            EstimatedRssiDbfs = frame.EstimatedRssiDbfs
        };
        var radio = new MeshtasticRadioReception(
            channel.Region, channel.RadioChannel, channel.FrequencyHz,
            channel.Profile.BandwidthHz, channel.Profile.SpreadingFactor, frame.CodingRateDenominator);
        _packetProcessor.Process(frame, quality, radio, channel.ChannelKeys);
    }

    private void HandleAcquisitionDiagnostic(ChannelConfiguration _, LoRaAcquisitionDiagnostic diagnostic)
    {
        AcquisitionDiagnostic?.Invoke(diagnostic);
    }
}
