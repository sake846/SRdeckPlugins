using System.Runtime.InteropServices;

namespace SRdeckPlugin.AribT98.Codec;

internal sealed record AribT98PrivacyMetricSet(int[] InvalidB0, double[] Pitch, double[] Gain,
    double[] Spectrum, double[] Voicing, double[] RawHamming, int[] SpeechTransitions);

internal enum AribT98AmbeKind { Speech, Silence, Tone, Erasure }

/// <summary>AMBE parameter scoring only; independent of software audio synthesis.</summary>
internal static partial class AribT98AmbeAnalysis
{
    private const int PrivacyCodeCount = 32767;
    private static readonly Lazy<bool> available = new(() =>
    {
        try { return ClassifyParameters(new byte[49]) == AribT98AmbeKind.Speech; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return false; }
    });
    internal static bool IsAvailable => available.Value;

    internal static unsafe AribT98PrivacyMetricSet ScorePrivacy(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, CancellationToken cancellationToken = default, bool allowParallel = true)
        => ScorePrivacyCore(blocks, slotMarkers, null, cancellationToken, allowParallel);

    internal static unsafe AribT98PrivacyMetricSet ScorePrivacy(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int[] keys, CancellationToken cancellationToken = default)
        => ScorePrivacyCore(blocks, slotMarkers, keys, cancellationToken, false);

    private static unsafe AribT98PrivacyMetricSet ScorePrivacyCore(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int[]? keys, CancellationToken cancellationToken, bool allowParallel)
    {
        if (slotMarkers.Length < 2 || blocks.Length != slotMarkers.Length * 49)
            throw new ArgumentException("Expected at least two packed 49-bit AMBE parameter blocks.");
        if (keys is { Length: 0 } || keys?.Any(key => key is < 1 or > PrivacyCodeCount) == true)
            throw new ArgumentOutOfRangeException(nameof(keys));
        cancellationToken.ThrowIfCancellationRequested();
        // Reject malformed input before starting workers, preserving the native
        // boundary's ArgumentException rather than wrapping it in AggregateException.
        foreach (byte marker in slotMarkers)
            if ((marker & 0x7c) != 0) throw new ArgumentException("Invalid AMBE slot marker.");
        foreach (byte bit in blocks)
            if (bit > 1) throw new ArgumentException("Expected binary AMBE parameters.");
        int resultCount = keys?.Length ?? PrivacyCodeCount;
        int blockCount = slotMarkers.Length;
        int batchSize = keys is null ? 1024 : 1;
        int batchCount = (resultCount + batchSize - 1) / batchSize;
        int[] invalid = new int[resultCount];
        double[] pitch = new double[resultCount];
        double[] gain = new double[resultCount];
        double[] spectrum = new double[resultCount];
        double[] voicing = new double[resultCount];
        double[] raw = new double[resultCount];
        int[] transitions = new int[resultCount];
        fixed (byte* blockPointer = blocks)
        fixed (byte* slotPointer = slotMarkers)
        fixed (int* invalidPointer = invalid)
        fixed (double* pitchPointer = pitch)
        fixed (double* gainPointer = gain)
        fixed (double* spectrumPointer = spectrum)
        fixed (double* voicingPointer = voicing)
        fixed (double* rawPointer = raw)
        fixed (int* transitionPointer = transitions)
        {
            // Parallel.For joins all workers before leaving this fixed scope.
            // Inputs are shared read-only; each batch writes a disjoint result range.
            nint blockAddress = (nint)blockPointer, slotAddress = (nint)slotPointer;
            nint invalidAddress = (nint)invalidPointer, pitchAddress = (nint)pitchPointer;
            nint gainAddress = (nint)gainPointer, spectrumAddress = (nint)spectrumPointer;
            nint voicingAddress = (nint)voicingPointer, rawAddress = (nint)rawPointer;
            nint transitionAddress = (nint)transitionPointer;
            void ScoreBatch(int batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = batch * batchSize;
                int count = Math.Min(batchSize, resultCount - offset);
                int firstKey = keys is null ? offset + 1 : keys[offset];
                int result = Native.ScorePrivacy((byte*)blockAddress, (byte*)slotAddress, blockCount, firstKey,
                    (int*)invalidAddress + offset, (double*)pitchAddress + offset, (double*)gainAddress + offset,
                    (double*)spectrumAddress + offset, (double*)voicingAddress + offset, (double*)rawAddress + offset,
                    (int*)transitionAddress + offset, count);
                if (result != count) throw new ArgumentException($"AMBE privacy scoring rejected the input ({result}).");
            }
            // ponytail: at most four workers and half the logical CPUs, leaving
            // headroom for reception; revisit only with measured receive stalls.
            int workers = allowParallel ? Math.Clamp(Environment.ProcessorCount / 2, 1, 4) : 1;
            if (workers > 1)
                Parallel.For(0, batchCount, new ParallelOptions
                { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, ScoreBatch);
            else
                for (int batch = 0; batch < batchCount; batch++) ScoreBatch(batch);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(invalid, pitch, gain, spectrum, voicing, raw, transitions);
    }

    internal static unsafe AribT98AmbeKind ClassifyParameters(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length != 49) throw new ArgumentException("Expected 49 AMBE parameters.");
        fixed (byte* pointer = parameters)
        {
            int kind = Native.ClassifyParameters(pointer, parameters.Length);
            if (kind < 0) throw new ArgumentException("Expected binary AMBE parameters.");
            return (AribT98AmbeKind)kind;
        }
    }

    private static unsafe partial class Native
    {
        private const string Library = "arib_t98_analysis_native";
        [LibraryImport(Library, EntryPoint = "arib_t98_score_privacy")]
        internal static partial int ScorePrivacy(byte* blocks, byte* slotMarkers, int blockCount, int firstKey,
            int* invalidB0, double* pitch, double* gain, double* spectrum, double* voicing,
            double* rawHamming, int* speechTransitions, int scoreCount);
        [LibraryImport(Library, EntryPoint = "arib_t98_classify_parameters")]
        internal static partial int ClassifyParameters(byte* parameters, int count);
    }
}
