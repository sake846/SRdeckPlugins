using System;
using System.Linq;
using System.Threading;
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
        try
        {
            MeshtasticRadioPacket packet = MeshtasticRadioPacketParser.Parse(frame.Payload);
            _lastMeshtasticPacket = packet;
            Interlocked.Increment(ref _parsedMeshtasticPackets);

            bool isDuplicate;
            int seenCount;
            lock (_recentPacketsGate)
            {
                isDuplicate = _recentPackets.TryGetValue(packet.Key, out int previousCount);
                seenCount = previousCount + 1;
                _recentPackets[packet.Key] = seenCount;
                if (!isDuplicate)
                {
                    _recentPacketOrder.Enqueue(packet.Key);
                    if (_recentPacketOrder.Count > RecentPacketCapacity)
                        _recentPackets.Remove(_recentPacketOrder.Dequeue());
                }
            }
            if (isDuplicate) Interlocked.Increment(ref _duplicateMeshtasticPackets);

            var quality = new MeshtasticLoRaReceptionQuality(
                state.LastDetection?.PeakToAverageDb,
                state.LastDetection?.DechirpedPeakHz,
                state.LastSynchronization?.UpChirpPeakHz,
                state.LastSynchronization?.DownChirpPeakHz,
                frame.IsPayloadCrcValid,
                frame.CorrectedCodewords);
            var radio = new MeshtasticRadioReception(
                channel.Region,
                channel.RadioChannel,
                channel.FrequencyHz,
                channel.Profile.BandwidthHz,
                channel.Profile.SpreadingFactor,
                frame.CodingRateDenominator);
            bool isDataDecoded = false;
            MeshtasticData? data = null;
            foreach (ChannelKey channelKey in channel.ChannelKeys.Where(value => value.Hash == packet.ChannelHash))
            {
                if (!MeshtasticChannelDecryptor.TryDecrypt(packet, channelKey.Name, channelKey.Key, out byte[] plaintext)) continue;
                if (MeshtasticDataParser.TryParse(plaintext, out data, out string ignoredParseError) && data is not null)
                {
                    break;
                }
            }

            if (data is not null)
            {
                isDataDecoded = true;
                if (MeshtasticApplicationPayloadParser.TryParse(data, out MeshtasticApplicationPayload? decoded, out string ignoredApplicationError))
                    data = data with { DecodedPayload = decoded };

                _lastMeshtasticData = data;
                Interlocked.Increment(ref _decodedMeshtasticData);
                MeshtasticDataReceived?.Invoke(new MeshtasticDataReception(packet, data, isDuplicate, seenCount, quality) { Radio = radio });
            }
            MeshtasticPacketReceived?.Invoke(new MeshtasticPacketReception(
                packet, isDuplicate, seenCount, isDataDecoded, quality, radio));
        }
        catch (ArgumentException)
        {
        }
    }

    private void HandleAcquisitionDiagnostic(ChannelConfiguration _, LoRaAcquisitionDiagnostic diagnostic)
    {
        AcquisitionDiagnostic?.Invoke(diagnostic);
    }
}
