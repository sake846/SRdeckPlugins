using SRdeckPlugin.MaritimeVhf.Models;
using SRdeckPlugin.MaritimeVhf.Protocols;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.MaritimeVhf.Dsp;

/// <summary>
/// VHF DSC AFSK receiver: 1,300/2,100 Hz at 1,200 bit/s. Character acquisition
/// is driven by the M.493 phasing sequence rather than a dot-pattern edge.
/// </summary>
internal sealed class DscSignalDecoder
{
    public const int SampleRateHz = 48_000;
    public const int BitRate = 1_200;
    public const int SamplesPerBit = SampleRateHz / BitRate;
    // The 80-character limit plus one decode interval fits within two seconds.
    private const int MaximumBufferedSamples = SampleRateHz * 2;
    private const int DecodeIntervalSamples = SampleRateHz / 5;
    private readonly List<float> audio = new(MaximumBufferedSamples);
    private readonly bool[] bits = new bool[MaximumBufferedSamples / SamplesPerBit];
    private readonly float[] bitConfidence = new float[MaximumBufferedSamples / SamplesPerBit];
    private readonly float[] markCos = new float[SamplesPerBit];
    private readonly float[] markSin = new float[SamplesPerBit];
    private readonly float[] spaceCos = new float[SamplesPerBit];
    private readonly float[] spaceSin = new float[SamplesPerBit];
    private int samplesSinceDecode;

    public DscSignalDecoder()
    {
        FillOscillator(1_300, markCos, markSin);
        FillOscillator(2_100, spaceCos, spaceSin);
    }

    public IReadOnlyList<DscMessage> ProcessAudio(
        ReadOnlySpan<float> samples,
        DateTimeOffset receivedAt,
        Guid streamId,
        long samplePosition)
    {
        if (samples.Length >= MaximumBufferedSamples)
        {
            audio.Clear();
            audio.AddRange(samples[^MaximumBufferedSamples..]);
        }
        else
        {
            int remove = Math.Max(0, audio.Count + samples.Length - MaximumBufferedSamples);
            if (remove > 0) audio.RemoveRange(0, remove);
            audio.AddRange(samples);
        }
        samplesSinceDecode += samples.Length;
        if (samplesSinceDecode < DecodeIntervalSamples) return [];
        samplesSinceDecode = 0;

        List<DscMessage>? messages = null;
        while (TryDecode(receivedAt, streamId, samplePosition, out DscMessage? message,
                   out int consumedSamples) && message is not null)
        {
            (messages ??= new List<DscMessage>(2)).Add(message);
            int remove = Math.Clamp(consumedSamples, 1, audio.Count);
            audio.RemoveRange(0, remove);
        }
        return messages ?? [];
    }

    public void Reset()
    {
        audio.Clear();
        samplesSinceDecode = 0;
    }

    private bool TryDecode(
        DateTimeOffset receivedAt,
        Guid streamId,
        long samplePosition,
        out DscMessage? message,
        out int consumedSamples)
    {
        message = null;
        consumedSamples = 0;
        if (audio.Count < SamplesPerBit * 240) return false;
        ReadOnlySpan<float> samples = CollectionsMarshal.AsSpan(audio);
        for (int phase = 0; phase < SamplesPerBit; phase++)
        {
            int bitCount = DemodulateBits(samples, phase, bits, bitConfidence);
            ReadOnlySpan<bool> phaseBits = bits.AsSpan(0, bitCount);
            ReadOnlySpan<float> phaseConfidence = bitConfidence.AsSpan(0, bitCount);
            for (int bitStart = 0; bitStart + 240 <= bitCount; bitStart++)
            {
                if (!TryAcquirePhasing(phaseBits, bitStart,
                        out int firstFormatCharacter)) continue;
                if (!TryReconstructLogical(phaseBits, phaseConfidence, bitStart, firstFormatCharacter,
                        out byte[] logical, out int finalRawCharacter)) continue;
                if (!DscProtocolParser.TryParse(logical, receivedAt, streamId,
                        samplePosition, out message)) continue;
                consumedSamples = phase +
                    (bitStart + (finalRawCharacter + 1) * DscSymbolCodec.BitsPerSymbol) *
                    SamplesPerBit;
                return true;
            }
        }
        return false;
    }

    private int DemodulateBits(ReadOnlySpan<float> samples, int phase, Span<bool> bits,
        Span<float> confidence)
    {
        int bitCount = (samples.Length - phase) / SamplesPerBit;
        for (int bit = 0; bit < bitCount; bit++)
        {
            int start = phase + bit * SamplesPerBit;
            double markI = 0, markQ = 0, spaceI = 0, spaceQ = 0;
            for (int index = 0; index < SamplesPerBit; index++)
            {
                float value = samples[start + index];
                markI += value * markCos[index];
                markQ += value * markSin[index];
                spaceI += value * spaceCos[index];
                spaceQ += value * spaceSin[index];
            }
            double markPower = markI * markI + markQ * markQ;
            double spacePower = spaceI * spaceI + spaceQ * spaceQ;
            // M.493: lower tone is Y=1, higher tone is B=0.
            bits[bit] = markPower > spacePower;
            confidence[bit] = (float)Math.Abs(markPower - spacePower);
        }
        return bitCount;
    }

    private static bool TryAcquirePhasing(
        ReadOnlySpan<bool> bits,
        int bitStart,
        out int firstFormatCharacter)
    {
        firstFormatCharacter = 0;
        for (int candidateDxParity = 0; candidateDxParity <= 1; candidateDxParity++)
        {
            int rxParity = 1 - candidateDxParity;
            int firstFormat = 12 + candidateDxParity;
            if (!TryReadSymbol(bits, bitStart, firstFormat, out byte format1) ||
                !DscProtocolParser.IsFormatSpecifier(format1) ||
                !TryReadSymbol(bits, bitStart, firstFormat + 2, out byte format2) ||
                format1 != format2 ||
                !TryReadSymbol(bits, bitStart, 12 + rxParity, out byte rx1) || rx1 != 105 ||
                !TryReadSymbol(bits, bitStart, 14 + rxParity, out byte rx0) || rx0 != 104)
                continue;
            int score = 0;
            for (int pair = 0; pair < 6; pair++)
            {
                if (TryReadSymbol(bits, bitStart, pair * 2 + candidateDxParity,
                        out byte dx) && dx == 125) score++;
                if (TryReadSymbol(bits, bitStart, pair * 2 + rxParity,
                        out byte rx) && rx == 111 - pair) score++;
            }
            if (score < 9) continue;
            firstFormatCharacter = firstFormat;
            return true;
        }
        return false;
    }

    private static bool TryReconstructLogical(
        ReadOnlySpan<bool> bits,
        ReadOnlySpan<float> confidence,
        int bitStart,
        int firstFormatCharacter,
        out byte[] logical,
        out int finalRawCharacter)
    {
        var values = new List<byte>(48);
        List<(int Index, byte Value, float Gain)>? alternatives = null;
        finalRawCharacter = 0;
        for (int rawCharacter = firstFormatCharacter;
             values.Count < 80;
             rawCharacter += 2)
        {
            bool dxValid = TryReadSymbol(bits, bitStart, rawCharacter, out byte dx);
            bool rxValid = TryReadSymbol(bits, bitStart, rawCharacter + 5, out byte rx);
            if (!dxValid && !rxValid) break;
            byte chosen = dxValid ? dx : rx;
            if (dxValid && rxValid && dx != rx)
            {
                float gain = SymbolConfidence(confidence, bitStart, rawCharacter + 5) -
                    SymbolConfidence(confidence, bitStart, rawCharacter);
                (alternatives ??= []).Add((values.Count, rx, gain));
            }
            values.Add(chosen);
            finalRawCharacter = rawCharacter + (rxValid ? 5 : 0);
            if (values.Count < 6) continue;
            Span<byte> candidate = CollectionsMarshal.AsSpan(values);
            if (DscProtocolParser.TryParse(candidate,
                    DateTimeOffset.UnixEpoch, Guid.Empty, 0, out _))
            {
                logical = values.ToArray();
                return true;
            }
            if (alternatives is null) continue;
            int bestIndex = -1;
            byte bestValue = 0;
            float bestGain = float.NegativeInfinity;
            foreach ((int index, byte alternate, float gain) in alternatives)
            {
                byte original = candidate[index];
                candidate[index] = alternate;
                if (DscProtocolParser.TryParse(candidate,
                        DateTimeOffset.UnixEpoch, Guid.Empty, 0, out _) && gain > bestGain)
                {
                    bestIndex = index;
                    bestValue = alternate;
                    bestGain = gain;
                }
                candidate[index] = original;
            }
            if (bestIndex < 0) continue;
            candidate[bestIndex] = bestValue;
            logical = candidate.ToArray();
            return true;
        }
        logical = [];
        return false;
    }

    private static float SymbolConfidence(ReadOnlySpan<float> confidence,
        int bitStart, int characterIndex)
    {
        int offset = bitStart + characterIndex * DscSymbolCodec.BitsPerSymbol;
        float total = 0;
        for (int bit = 0; bit < DscSymbolCodec.BitsPerSymbol; bit++)
            total += confidence[offset + bit];
        return total;
    }

    private static bool TryReadSymbol(
        ReadOnlySpan<bool> bits,
        int bitStart,
        int characterIndex,
        out byte symbol)
    {
        int offset = bitStart + characterIndex * DscSymbolCodec.BitsPerSymbol;
        if (offset < 0 || offset + DscSymbolCodec.BitsPerSymbol > bits.Length)
        {
            symbol = 0;
            return false;
        }
        return DscSymbolCodec.TryDecode(bits.Slice(offset, DscSymbolCodec.BitsPerSymbol),
            out symbol);
    }

    private static void FillOscillator(int frequencyHz, Span<float> cosine, Span<float> sine)
    {
        double step = 2 * Math.PI * frequencyHz / SampleRateHz;
        for (int index = 0; index < cosine.Length; index++)
        {
            cosine[index] = (float)Math.Cos(step * index);
            sine[index] = (float)-Math.Sin(step * index);
        }
    }
}
