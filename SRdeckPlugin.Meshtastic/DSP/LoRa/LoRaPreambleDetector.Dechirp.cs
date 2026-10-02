using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SRdeck.DSP;

namespace SRdeckPlugin.Meshtastic.Dsp;

internal sealed partial class LoRaPreambleDetector
{
    internal static double Dechirp(ReadOnlySpan<Complex> samples,
        ReadOnlySpan<Complex> reference, Span<Complex> output, bool dechirpUpChirp)
    {
        if (reference.Length != samples.Length || output.Length != samples.Length)
            throw new ArgumentException("Expected matching sample, chirp and FFT lengths.");

        int n = 0;
        if (Avx.IsSupported)
        {
            ref float source = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Complex, float>(samples));
            ref float chirps = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Complex, float>(reference));
            ref float destination = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Complex, float>(output));
            Vector256<float> conjugate = dechirpUpChirp
                ? Vector256<float>.Zero
                : Vector256.Create(0f, -0f, 0f, -0f, 0f, -0f, 0f, -0f);
            for (; n <= samples.Length - 4; n += 4)
            {
                nuint offset = (nuint)n * 2;
                Vector256<float> sample = Vector256.LoadUnsafe(ref source, offset);
                Vector256<float> chirp = Avx.Xor(Vector256.LoadUnsafe(ref chirps, offset), conjugate);
                Vector256<float> real = Avx.Shuffle(chirp, chirp, 0xA0);
                Vector256<float> imaginary = Avx.Shuffle(chirp, chirp, 0xF5);
                Vector256<float> swapped = Avx.Shuffle(sample, sample, 0xB1);
                // Separate multiply/add preserves scalar rounding (no FMA).
                Avx.AddSubtract(Avx.Multiply(sample, real), Avx.Multiply(swapped, imaginary))
                    .StoreUnsafe(ref destination, offset);
            }
        }

        // Preserve both the float square/add and the original double summation
        // order, including the silence threshold. Reuse the FFT output buffer.
        double totalPower = 0;
        for (int i = 0; i < n; i++)
        {
            Complex sample = output[i];
            totalPower += sample.X * sample.X + sample.Y * sample.Y;
        }
        for (; n < samples.Length; n++)
        {
            Complex sample = samples[n];
            Complex chirp = reference[n];
            if (!dechirpUpChirp) chirp.Y = -chirp.Y;
            float x = sample.X * chirp.X - sample.Y * chirp.Y;
            float y = sample.X * chirp.Y + sample.Y * chirp.X;
            output[n] = new Complex { X = x, Y = y };
            totalPower += x * x + y * y;
        }
        return totalPower;
    }
}
