using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Protocols;

namespace SRdeckPlugin.Meshtastic.Services;

internal sealed record MeshtasticChannelKey(string Name, byte[] Key, byte Hash);

internal readonly record struct MeshtasticPacketStatistics(
    long ParsedPackets, long DuplicatePackets, long DecodedData,
    MeshtasticRadioPacket? LastPacket, MeshtasticData? LastData);

/// <summary>
/// Owns packet interpretation and bounded duplicate history for one receive service.
/// Detector callbacks may run concurrently. Duplicate state is protected by its own gate;
/// Stop/Start clears that history after the worker drains, while statistics reset keeps it.
/// </summary>
internal sealed class MeshtasticPacketProcessor
{
    private const int RecentPacketCapacity = 4096;
    private readonly Dictionary<MeshtasticPacketKey, int> _recentPackets = new();
    private readonly Queue<MeshtasticPacketKey> _recentPacketOrder = new();
    private readonly object _recentPacketsGate = new();
    private long _parsedPackets;
    private long _duplicatePackets;
    private long _decodedData;
    private MeshtasticRadioPacket? _lastPacket;
    private MeshtasticData? _lastData;

    public event Action<MeshtasticDataReception>? DataReceived;
    public event Action<MeshtasticPacketReception>? PacketReceived;

    public MeshtasticPacketStatistics Snapshot => new(
        Interlocked.Read(ref _parsedPackets), Interlocked.Read(ref _duplicatePackets),
        Interlocked.Read(ref _decodedData), _lastPacket, _lastData);

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _parsedPackets, 0);
        Interlocked.Exchange(ref _duplicatePackets, 0);
        Interlocked.Exchange(ref _decodedData, 0);
    }

    public void ResetStream()
    {
        lock (_recentPacketsGate)
        {
            _recentPackets.Clear();
            _recentPacketOrder.Clear();
        }
    }

    public void Process(LoRaPayloadFrame frame, MeshtasticLoRaReceptionQuality quality,
        MeshtasticRadioReception radio, IReadOnlyList<MeshtasticChannelKey> channelKeys)
    {
        if (frame.IsPayloadCrcValid is false || frame.Payload.Length < MeshtasticRadioPacketParser.HeaderLength) return;
        try
        {
            MeshtasticRadioPacket packet = MeshtasticRadioPacketParser.Parse(frame.Payload);
            _lastPacket = packet;
            Interlocked.Increment(ref _parsedPackets);

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
            if (isDuplicate) Interlocked.Increment(ref _duplicatePackets);

            bool isDataDecoded = false;
            MeshtasticData? data = null;
            foreach (MeshtasticChannelKey channelKey in channelKeys.Where(value => value.Hash == packet.ChannelHash))
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

                _lastData = data;
                Interlocked.Increment(ref _decodedData);
                DataReceived?.Invoke(new MeshtasticDataReception(packet, data, isDuplicate, seenCount, quality) { Radio = radio });
            }
            PacketReceived?.Invoke(new MeshtasticPacketReception(
                packet, isDuplicate, seenCount, isDataDecoded, quality, radio));
        }
        catch (ArgumentException)
        {
        }
    }
}
