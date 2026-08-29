using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Hfdl.Dsp;

public static partial class HfdlPhysicalLayer
{
    /// <summary>
    /// Builds a complete transmit waveform for physical-layer reference vectors
    /// and receiver self-tests. The live receive path does not call this method.
    /// </summary>
    public static Complex32[] BuildBurst(ReadOnlySpan<byte> mpdu, HfdlPhysicalMode mode)
    {
        bool[] userBits = BytesToBitsLsbFirst(mpdu, mode.UserBitCount);
        bool[] coded = ConvolutionalEncode(userBits);
        if (mode.DataRate == 300) coded = coded.SelectMany(bit => new[] { bit, bit }).ToArray();
        bool[] interleaved = Interleave(coded, mode.InterleaverColumns, mode.LongInterleaver);
        Complex32[] dataSymbols = MapAndScramble(interleaved, mode.ModulationOrder);
        ReadOnlySpan<bool> shiftedM1 = ShiftedM1(mode.M1Shift);

        var symbols = new List<Complex32>(
            PrekeySymbols + PreambleSymbols + mode.FrameCount * SymbolsPerFrame);
        for (int index = 0; index < PrekeySymbols; index++)
            symbols.Add(Bpsk((index & 1) != 0));
        AppendBpsk(symbols, ASequence);
        AppendBpsk(symbols, ASequence);
        AppendBpsk(symbols, shiftedM1);
        AppendBpsk(symbols, shiftedM1[..ProbeSymbolsPerFrame]);
        AppendBpsk(symbols, TSequence);
        for (int frame = 0; frame < mode.FrameCount; frame++)
        {
            symbols.AddRange(dataSymbols.AsSpan(
                frame * DataSymbolsPerFrame, DataSymbolsPerFrame).ToArray());
            AppendBpsk(symbols, shiftedM1[..ProbeSymbolsPerFrame]);
        }
        return symbols.ToArray();
    }
}
