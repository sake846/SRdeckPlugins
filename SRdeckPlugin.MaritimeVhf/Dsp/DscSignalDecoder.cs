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
    private const int MaximumBufferedSamples = SampleRateHz * 3;
    private const int DecodeIntervalSamples = SampleRateHz / 5;
    private readonly List<float> audio = new(MaximumBufferedSamples);
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
        audio.AddRange(samples);
        samplesSinceDecode += samples.Length;
        if (audio.Count > MaximumBufferedSamples)
            audio.RemoveRange(0, audio.Count - MaximumBufferedSamples);
        if (samplesSinceDecode < DecodeIntervalSamples) return [];
        samplesSinceDecode = 0;

        var messages = new List<DscMessage>(2);
        while (TryDecode(receivedAt, streamId, samplePosition, out DscMessage? message,
                   out int consumedSamples) && message is not null)
        {
            messages.Add(message);
            int remove = Math.Clamp(consumedSamples, 1, audio.Count);
            audio.RemoveRange(0, remove);
        }
        return messages;
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
            bool[] bits = DemodulateBits(samples, phase);
            for (int bitStart = 0; bitStart + 240 <= bits.Length; bitStart++)
            {
                if (!TryAcquirePhasing(bits, bitStart,
                        out int firstFormatCharacter)) continue;
                if (!TryReconstructLogical(bits, bitStart, firstFormatCharacter,
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

    private bool[] DemodulateBits(ReadOnlySpan<float> samples, int phase)
    {
        int bitCount = (samples.Length - phase) / SamplesPerBit;
        var bits = new bool[bitCount];
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
        }
        return bits;
    }

    private static bool TryAcquirePhasing(
        ReadOnlySpan<bool> bits,
        int bitStart,
        out int firstFormatCharacter)
    {
        firstFormatCharacter = 0;
        for (int candidateDxParity = 0; candidateDxParity <= 1; candidateDxParity++)
        {
            int score = 0;
            int rxParity = 1 - candidateDxParity;
            for (int pair = 0; pair < 6; pair++)
            {
                if (TryReadSymbol(bits, bitStart, pair * 2 + candidateDxParity,
                        out byte dx) && dx == 125) score++;
                if (TryReadSymbol(bits, bitStart, pair * 2 + rxParity,
                        out byte rx) && rx == 111 - pair) score++;
            }
            int firstFormat = 12 + candidateDxParity;
            if (!TryReadSymbol(bits, bitStart, 12 + rxParity, out byte rx1) || rx1 != 105 ||
                !TryReadSymbol(bits, bitStart, 14 + rxParity, out byte rx0) || rx0 != 104 ||
                !TryReadSymbol(bits, bitStart, firstFormat, out byte format1) ||
                !TryReadSymbol(bits, bitStart, firstFormat + 2, out byte format2) ||
                format1 != format2 || !DscProtocolParser.IsFormatSpecifier(format1) || score < 9)
                continue;
            firstFormatCharacter = firstFormat;
            return true;
        }
        return false;
    }

    private static bool TryReconstructLogical(
        ReadOnlySpan<bool> bits,
        int bitStart,
        int firstFormatCharacter,
        out byte[] logical,
        out int finalRawCharacter)
    {
        var values = new List<byte>(48);
        finalRawCharacter = 0;
        for (int rawCharacter = firstFormatCharacter;
             values.Count < 80;
             rawCharacter += 2)
        {
            bool dxValid = TryReadSymbol(bits, bitStart, rawCharacter, out byte dx);
            bool rxValid = TryReadSymbol(bits, bitStart, rawCharacter + 5, out byte rx);
            if (!dxValid && !rxValid) break;
            values.Add(dxValid ? dx : rx);
            finalRawCharacter = rawCharacter + (rxValid ? 5 : 0);
            if (values.Count >= 6 &&
                DscProtocolParser.TryParse(CollectionsMarshal.AsSpan(values),
                    DateTimeOffset.UnixEpoch, Guid.Empty, 0, out _))
            {
                logical = values.ToArray();
                return true;
            }
        }
        logical = [];
        return false;
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
