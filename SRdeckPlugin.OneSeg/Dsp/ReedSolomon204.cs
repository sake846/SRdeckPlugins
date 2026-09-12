namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class ReedSolomon204
{
    private readonly GaloisField field = new();
    private readonly int[] received = new int[255];
    private readonly int[] syndrome = new int[16];
    private readonly int[] locations = new int[16];
    private readonly int[] magnitudes = new int[16];

    public bool TryCorrect(ReadOnlySpan<byte> packet, out byte[] transportPacket, out int correctedErrors)
    {
        Span<byte> output = stackalloc byte[188];
        bool valid = TryCorrect(packet, output, out correctedErrors);
        transportPacket = valid ? output.ToArray() : [];
        return valid;
    }

    // Scratch is owned by this single-threaded decoder and survives only one call.
    public bool TryCorrect(ReadOnlySpan<byte> packet, Span<byte> output, out int correctedErrors)
    {
        correctedErrors = 0;
        if (packet.Length != 204) return false;
        if (output.Length < 188) throw new ArgumentException("The output must hold 188 bytes.", nameof(output));
        bool clean = true;
        for (int index = 0; index < syndrome.Length; index++)
        {
            // Leading shortened zero symbols do not affect Horner evaluation.
            int evaluation = packet[0], value = field.Exp(index);
            for (int term = 1; term < packet.Length; term++)
                evaluation = field.Multiply(evaluation, value) ^ packet[term];
            syndrome[syndrome.Length - 1 - index] = evaluation;
            clean &= evaluation == 0;
        }
        if (clean)
        {
            packet[..188].CopyTo(output);
            return output[0] == 0x47;
        }
        field.ResetScratch();
        Array.Clear(received, 0, 51);
        for (int index = 0; index < packet.Length; index++) received[index + 51] = packet[index];
        if (!TryRunEuclidean(field.BuildMonomial(16, 1), field.CreatePolynomial(syndrome), 16,
            out Polynomial sigma, out Polynomial omega)) return false;
        int locationCount = FindErrorLocations(sigma, locations);
        if (locationCount < 0 || !FindErrorMagnitudes(omega, locations.AsSpan(0, locationCount), magnitudes)) return false;
        for (int index = 0; index < locationCount; index++)
        {
            int position = received.Length - 1 - field.Log(locations[index]);
            // Errors in the 16 parity bytes are correctable as well. Only
            // locations in the 51 implicit shortened symbols are invalid.
            if (position < 51 || position >= 255) return false;
            received[position] ^= magnitudes[index];
        }
        correctedErrors = locationCount;
        for (int index = 0; index < 188; index++) output[index] = (byte)received[index + 51];
        return output[0] == 0x47;
    }

    private bool TryRunEuclidean(Polynomial first, Polynomial second, int r,
        out Polynomial sigma, out Polynomial omega)
    {
        sigma = omega = field.Zero;
        if (first.Degree < second.Degree) (first, second) = (second, first);
        Polynomial rLast = first, current = second;
        Polynomial tLast = field.Zero, t = field.One;
        while (current.Degree >= r / 2)
        {
            Polynomial previous = rLast;
            Polynomial previousT = tLast;
            rLast = current;
            tLast = t;
            if (rLast.IsZero) return false;
            current = previous;
            Polynomial quotient = field.Zero;
            int denominatorInverse = field.Inverse(rLast.LeadingCoefficient);
            while (current.Degree >= rLast.Degree && !current.IsZero)
            {
                int degreeDifference = current.Degree - rLast.Degree;
                int scale = field.Multiply(current.LeadingCoefficient, denominatorInverse);
                quotient = quotient.AddOrSubtract(field.BuildMonomial(degreeDifference, scale));
                current = current.AddOrSubtract(rLast.MultiplyByMonomial(degreeDifference, scale));
            }
            t = quotient.Multiply(tLast).AddOrSubtract(previousT);
        }
        int sigmaAtZero = t.GetCoefficient(0);
        if (sigmaAtZero == 0) return false;
        int inverse = field.Inverse(sigmaAtZero);
        sigma = t.Multiply(inverse);
        omega = current.Multiply(inverse);
        return true;
    }

    private int FindErrorLocations(Polynomial locator, Span<int> locations)
    {
        int count = locator.Degree;
        if (count == 1) { locations[0] = locator.GetCoefficient(1); return 1; }
        int found = 0;
        for (int value = 1; value < 256 && found < count; value++)
            if (locator.EvaluateAt(value) == 0) locations[found++] = field.Inverse(value);
        return found == count ? count : -1;
    }

    private bool FindErrorMagnitudes(Polynomial evaluator, ReadOnlySpan<int> locations, Span<int> magnitudes)
    {
        for (int index = 0; index < locations.Length; index++)
        {
            if (locations[index] == 0) return false;
            int inverse = field.Inverse(locations[index]);
            int denominator = 1;
            for (int other = 0; other < locations.Length; other++)
                if (index != other) denominator = field.Multiply(denominator,
                    field.Multiply(locations[other], inverse) ^ 1);
            if (denominator == 0) return false;
            magnitudes[index] = field.Multiply(evaluator.EvaluateAt(inverse), field.Inverse(denominator));
        }
        return true;
    }

    private sealed class GaloisField
    {
        private readonly int[] exp = new int[512];
        private readonly int[] log = new int[256];
        private readonly List<Polynomial> scratch = [];
        private int used;
        public Polynomial Zero { get; }
        public Polynomial One { get; }

        public GaloisField()
        {
            int value = 1;
            for (int index = 0; index < 255; index++)
            {
                exp[index] = value;
                log[value] = index;
                value <<= 1;
                if ((value & 0x100) != 0) value ^= 0x11d;
            }
            for (int index = 255; index < exp.Length; index++) exp[index] = exp[index - 255];
            Zero = new Polynomial(this, [0]);
            One = new Polynomial(this, [1]);
        }

        public void ResetScratch() => used = 0;
        public Polynomial Rent(int length)
        {
            if (used == scratch.Count) scratch.Add(new Polynomial(this, [0]));
            Polynomial result = scratch[used++];
            result.Initialize(length);
            return result;
        }
        public Polynomial CreatePolynomial(ReadOnlySpan<int> source)
        {
            Polynomial result = Rent(source.Length);
            source.CopyTo(result.Coefficients);
            return result.Normalize();
        }

        public int Exp(int power) => exp[power];
        public int Log(int value) => value == 0 ? throw new InvalidOperationException("log(0)") : log[value];
        public int Inverse(int value) => value == 0 ? throw new InvalidOperationException("inverse(0)") : exp[255 - log[value]];
        public int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : exp[log[a] + log[b]];
        public Polynomial BuildMonomial(int degree, int coefficient)
        {
            if (degree < 0) throw new ArgumentOutOfRangeException(nameof(degree));
            if (coefficient == 0) return Zero;
            Polynomial result = Rent(degree + 1);
            result.Coefficients[0] = coefficient;
            return result;
        }
    }

    private sealed class Polynomial
    {
        private readonly GaloisField field;
        private int[] coefficients = new int[33];
        private int start, length;
        public Span<int> Coefficients => coefficients.AsSpan(start, length - start);
        public int Degree => length - start - 1;
        public bool IsZero => coefficients[start] == 0;
        public int LeadingCoefficient => coefficients[start];

        public Polynomial(GaloisField field, ReadOnlySpan<int> source)
        {
            this.field = field;
            Initialize(source.Length);
            source.CopyTo(Coefficients);
            Normalize();
        }

        public void Initialize(int count)
        {
            if (coefficients.Length < count) coefficients = new int[count];
            start = 0;
            length = count;
            Coefficients.Clear();
        }

        public Polynomial Normalize()
        {
            while (start < length - 1 && coefficients[start] == 0) start++;
            return this;
        }

        public int GetCoefficient(int degree) => coefficients[length - 1 - degree];
        public int EvaluateAt(int value)
        {
            if (value == 0) return GetCoefficient(0);
            int result = coefficients[start];
            for (int index = start + 1; index < length; index++)
                result = field.Multiply(result, value) ^ coefficients[index];
            return result;
        }

        public Polynomial AddOrSubtract(Polynomial other)
        {
            if (IsZero) return other;
            if (other.IsZero) return this;
            ReadOnlySpan<int> small = Coefficients, large = other.Coefficients;
            if (small.Length > large.Length) { small = other.Coefficients; large = Coefficients; }
            Polynomial sum = field.Rent(large.Length);
            Span<int> destination = sum.Coefficients;
            large.CopyTo(destination);
            int difference = large.Length - small.Length;
            for (int index = 0; index < small.Length; index++) destination[index + difference] ^= small[index];
            return sum.Normalize();
        }

        public Polynomial Multiply(Polynomial other)
        {
            if (IsZero || other.IsZero) return field.Zero;
            ReadOnlySpan<int> leftTerms = Coefficients, rightTerms = other.Coefficients;
            Polynomial product = field.Rent(leftTerms.Length + rightTerms.Length - 1);
            Span<int> destination = product.Coefficients;
            for (int left = 0; left < leftTerms.Length; left++)
            for (int right = 0; right < rightTerms.Length; right++)
                destination[left + right] ^= field.Multiply(leftTerms[left], rightTerms[right]);
            return product.Normalize();
        }

        public Polynomial Multiply(int scalar)
        {
            if (scalar == 0) return field.Zero;
            if (scalar == 1) return this;
            ReadOnlySpan<int> source = Coefficients;
            Polynomial product = field.Rent(source.Length);
            Span<int> destination = product.Coefficients;
            for (int index = 0; index < source.Length; index++)
                destination[index] = field.Multiply(source[index], scalar);
            return product.Normalize();
        }

        public Polynomial MultiplyByMonomial(int degree, int coefficient)
        {
            if (coefficient == 0) return field.Zero;
            ReadOnlySpan<int> source = Coefficients;
            Polynomial product = field.Rent(source.Length + degree);
            Span<int> destination = product.Coefficients;
            for (int index = 0; index < source.Length; index++)
                destination[index] = field.Multiply(source[index], coefficient);
            return product.Normalize();
        }
    }
}
