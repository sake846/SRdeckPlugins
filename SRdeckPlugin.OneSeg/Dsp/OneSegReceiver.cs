using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class OneSegReceiver
{
    private readonly OfdmSynchronizer synchronizer = new();
    private readonly OfdmCarrierDecoder carriers = new();
    private readonly TimeDeinterleaver time = new();
    private readonly BitDeinterleaver bits = new();
    private readonly ViterbiDecoder viterbi = new();
    private readonly ByteDeinterleaver bytes = new();
    private readonly OuterPacketDecoder outer = new();
    private ComplexF[] frequencyBuffer = [];
    private ComplexF[] temporalBuffer = [];
    private ComplexF[] previousDqpsk = [];
    private ComplexF[] differentialBuffer = [];
    private sbyte[] softMappedBuffer = [];
    private sbyte[] softBitDeinterleaverBuffer = [];
    private byte[] viterbiBuffer = [];
    private byte[] byteDeinterleaverBuffer = [];
    private IsdbTMode bufferedMode = IsdbTMode.Unknown;
    private IsdbTModulation bufferedModulation = IsdbTModulation.Unused;
    private bool hasPreviousDqpsk;
    private IsdbTCodeRate configuredRate = IsdbTCodeRate.Unused;
    private long symbols, frames, viterbiBytes;
    private double mer;
    private double channelPower;
    private bool wasLocked;
    private readonly Action<byte[]> publishTransportPacket;

    public OneSegReceiver() => publishTransportPacket = packet => TransportPacketDecoded?.Invoke(packet);

    public event Action<byte[]>? TransportPacketDecoded;

    internal TmccDecoder Tmcc => carriers.Tmcc;
    internal OfdmSynchronizer Synchronizer => synchronizer;
    internal OfdmCarrierDecoder CarrierDecoder => carriers;

    public OneSegDiagnostics Diagnostics => new(
        DateTimeOffset.Now, synchronizer.Mode, synchronizer.GuardInterval,
        synchronizer.IsLocked, carriers.Tmcc.IsLocked, synchronizer.FrequencyOffsetHz,
        mer, symbols, frames, viterbiBytes, outer.CorrectedPackets,
        outer.RejectedPackets, 0, 0, carriers.Tmcc.Parameters,
        channelPower > 0 ? 10 * Math.Log10(channelPower) : double.NegativeInfinity);

    public void Reset()
    {
        synchronizer.Reset();
        carriers.Reset();
        time.Reset();
        bits.Reset();
        viterbi.Reset(IsdbTCodeRate.Rate1Over2);
        bytes.Reset();
        outer.Reset();
        bufferedMode = IsdbTMode.Unknown;
        bufferedModulation = IsdbTModulation.Unused;
        hasPreviousDqpsk = false;
        configuredRate = IsdbTCodeRate.Unused;
        symbols = frames = viterbiBytes = 0;
        mer = 0;
        channelPower = 0;
        wasLocked = false;
    }

    public void Process(ReadOnlySpan<Complex32> samples)
    {
        UpdateChannelPower(samples);
        synchronizer.Append(samples);
        if (wasLocked && !synchronizer.IsLocked)
        {
            carriers.Reset();
            time.Reset();
            bits.Reset();
            viterbi.Reset(IsdbTCodeRate.Rate1Over2);
            bytes.Reset();
            outer.Reset();
            bufferedMode = IsdbTMode.Unknown;
            bufferedModulation = IsdbTModulation.Unused;
            hasPreviousDqpsk = false;
            configuredRate = IsdbTCodeRate.Unused;
            mer = 0;
        }
        while (synchronizer.TryRead(out OfdmSymbol symbol))
        {
            wasLocked = true;
            symbols++;
            EqualizedOneSegSymbol equalized = carriers.Process(symbol);
            mer = mer == 0 ? equalized.MerDb : 0.9 * mer + 0.1 * equalized.MerDb;
            if (equalized.FrameEnded) frames++;
            TmccLayerParameters? layer = equalized.Tmcc?.LayerA;
            if (layer is null || equalized.Tmcc?.IsParityValid != true ||
                layer.SegmentCount < 1 || layer.Modulation == IsdbTModulation.Unused ||
                layer.CodeRate == IsdbTCodeRate.Unused || layer.TimeInterleavingLength < 0) continue;
            int packetsPerFrame = PacketsPerFrame(symbol.Mode, layer);
            outer.ConfigureFramePackets(packetsPerFrame);
            if (configuredRate != layer.CodeRate)
            {
                configuredRate = layer.CodeRate;
                viterbi.Reset(configuredRate);
                bits.Reset();
                // TMCC parameters first become available on symbol 203.  The byte
                // deinterleaver commutator must therefore start at that symbol's
                // absolute byte phase, not at branch zero.  A frame contains 204
                // symbols and packetsPerFrame bytes per symbol after Viterbi.
                int byteBranch = ((IsdbTParameters.SymbolsPerFrame - 1) * packetsPerFrame) % 12;
                bytes.Reset(byteBranch);
            }
            EnsurePipelineBuffers(symbol.Mode, layer.Modulation);
            FrequencyDeinterleaver.Process(equalized.Data, symbol.Mode, frequencyBuffer);
            time.Process(frequencyBuffer, symbol.Mode, layer.TimeInterleavingLength, temporalBuffer);
            ReadOnlySpan<ComplexF> demapInput = layer.Modulation == IsdbTModulation.Dqpsk
                ? DifferentialDecode(temporalBuffer)
                : temporalBuffer;
            int mappedCount = SymbolDemapper.DemapSoft(
                demapInput, layer.Modulation, softMappedBuffer);
            bits.ProcessSoft(softMappedBuffer.AsSpan(0, mappedCount), layer.Modulation,
                softBitDeinterleaverBuffer);
            int decodedCount = viterbi.ProcessSoft(
                softBitDeinterleaverBuffer.AsSpan(0, mappedCount),
                viterbiBuffer);
            viterbiBytes += decodedCount;
            bytes.Process(viterbiBuffer.AsSpan(0, decodedCount), byteDeinterleaverBuffer);
            outer.Process(byteDeinterleaverBuffer.AsSpan(0, decodedCount), publishTransportPacket);
        }
    }

    private void UpdateChannelPower(ReadOnlySpan<Complex32> samples)
    {
        if (samples.IsEmpty) return;
        double sum = 0;
        int count = 0;
        foreach (Complex32 sample in samples)
        {
            double power = sample.I * sample.I + sample.Q * sample.Q;
            if (!double.IsFinite(power)) continue;
            sum += power;
            count++;
        }
        if (count == 0) return;
        double blockPower = sum / count;
        channelPower = channelPower <= 0 ? blockPower : channelPower + 0.1 * (blockPower - channelPower);
    }

    private static int PacketsPerFrame(IsdbTMode mode, TmccLayerParameters layer)
    {
        (int numerator, int denominator) = layer.CodeRate switch
        {
            IsdbTCodeRate.Rate1Over2 => (1,2), IsdbTCodeRate.Rate2Over3 => (2,3),
            IsdbTCodeRate.Rate3Over4 => (3,4), IsdbTCodeRate.Rate5Over6 => (5,6),
            IsdbTCodeRate.Rate7Over8 => (7,8), _ => (0,1)
        };
        return 12 * IsdbTParameters.ModeMultiplier(mode) *
            IsdbTParameters.BitsPerCarrier(layer.Modulation) * numerator / denominator;
    }

    private void EnsurePipelineBuffers(IsdbTMode mode, IsdbTModulation modulation)
    {
        if (bufferedMode == mode && bufferedModulation == modulation) return;
        int carrierCount = IsdbTParameters.DataCarriers(mode);
        int bitCount = carrierCount * SymbolDemapper.BitsPerSymbol(modulation);
        frequencyBuffer = new ComplexF[carrierCount];
        temporalBuffer = new ComplexF[carrierCount];
        previousDqpsk = new ComplexF[carrierCount];
        differentialBuffer = new ComplexF[carrierCount];
        softMappedBuffer = new sbyte[bitCount];
        softBitDeinterleaverBuffer = new sbyte[bitCount];
        viterbiBuffer = new byte[ViterbiDecoder.MaximumOutputBytes(bitCount)];
        byteDeinterleaverBuffer = new byte[viterbiBuffer.Length];
        bufferedMode = mode;
        bufferedModulation = modulation;
        hasPreviousDqpsk = false;
    }

    private ReadOnlySpan<ComplexF> DifferentialDecode(ComplexF[] current)
    {
        if (hasPreviousDqpsk)
            for (int index = 0; index < current.Length; index++)
                differentialBuffer[index] = current[index] * previousDqpsk[index].Conjugate();
        else
            Array.Clear(differentialBuffer);
        (previousDqpsk, temporalBuffer) = (temporalBuffer, previousDqpsk);
        hasPreviousDqpsk = true;
        return differentialBuffer;
    }
}
