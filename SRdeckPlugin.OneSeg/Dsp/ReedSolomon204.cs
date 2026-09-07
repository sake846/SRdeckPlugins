namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class ReedSolomon204
{
    private readonly GaloisField field = new();

    public bool TryCorrect(ReadOnlySpan<byte> packet, out byte[] transportPacket, out int correctedErrors)
    {
        transportPacket = [];
        correctedErrors = 0;
        if (packet.Length != 204) return false;
        var received = new int[255];
        for (int index = 0; index < packet.Length; index++) received[index + 51] = packet[index];
        var polynomial = new Polynomial(field, received);
        var syndrome = new int[16];
        bool clean = true;
        for (int index = 0; index < syndrome.Length; index++)
        {
            int evaluation = polynomial.EvaluateAt(field.Exp(index));
            syndrome[syndrome.Length - 1 - index] = evaluation;
            clean &= evaluation == 0;
        }
        if (!clean)
        {
            try
            {
                (Polynomial sigma, Polynomial omega) = RunEuclidean(field.BuildMonomial(16, 1),
                    new Polynomial(field, syndrome), 16);
                int[] locations = FindErrorLocations(sigma);
                int[] magnitudes = FindErrorMagnitudes(omega, locations);
                for (int index = 0; index < locations.Length; index++)
                {
                    int position = received.Length - 1 - field.Log(locations[index]);
                    // Errors in the 16 parity bytes are correctable as well.  Only
                    // locations in the 51 implicit shortened symbols are invalid.
                    if (position < 51 || position >= 255) return false;
                    received[position] ^= magnitudes[index];
                }
                correctedErrors = locations.Length;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        transportPacket = new byte[188];
        for (int index = 0; index < transportPacket.Length; index++)
            transportPacket[index] = (byte)received[index + 51];
        return transportPacket[0] == 0x47;
    }

    private (Polynomial Sigma, Polynomial Omega) RunEuclidean(Polynomial first, Polynomial second, int r)
    {
        if (first.Degree < second.Degree) (first, second) = (second, first);
        Polynomial rLast = first, current = second;
        Polynomial tLast = field.Zero, t = field.One;
        while (current.Degree >= r / 2)
        {
            Polynomial previous = rLast;
            Polynomial previousT = tLast;
            rLast = current;
            tLast = t;
            if (rLast.IsZero) throw new InvalidOperationException("Invalid RS syndrome.");
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
        if (sigmaAtZero == 0) throw new InvalidOperationException("RS locator has no constant term.");
        int inverse = field.Inverse(sigmaAtZero);
        return (t.Multiply(inverse), current.Multiply(inverse));
    }

    private int[] FindErrorLocations(Polynomial locator)
    {
        int count = locator.Degree;
        if (count == 1) return [locator.GetCoefficient(1)];
        var locations = new int[count];
        int found = 0;
        for (int value = 1; value < 256 && found < count; value++)
            if (locator.EvaluateAt(value) == 0) locations[found++] = field.Inverse(value);
        if (found != count) throw new InvalidOperationException("RS error locations are inconsistent.");
        return locations;
    }

    private int[] FindErrorMagnitudes(Polynomial evaluator, int[] locations)
    {
        var magnitudes = new int[locations.Length];
        for (int index = 0; index < locations.Length; index++)
        {
            int inverse = field.Inverse(locations[index]);
            int denominator = 1;
            for (int other = 0; other < locations.Length; other++)
                if (index != other) denominator = field.Multiply(denominator,
                    field.Multiply(locations[other], inverse) ^ 1);
            magnitudes[index] = field.Multiply(evaluator.EvaluateAt(inverse), field.Inverse(denominator));
        }
        return magnitudes;
    }

    private sealed class GaloisField
    {
        private readonly int[] exp = new int[512];
        private readonly int[] log = new int[256];
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

        public int Exp(int power) => exp[power];
        public int Log(int value) => value == 0 ? throw new InvalidOperationException("log(0)") : log[value];
        public int Inverse(int value) => value == 0 ? throw new InvalidOperationException("inverse(0)") : exp[255 - log[value]];
        public int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : exp[log[a] + log[b]];
        public Polynomial BuildMonomial(int degree, int coefficient)
        {
            if (degree < 0) throw new ArgumentOutOfRangeException(nameof(degree));
            if (coefficient == 0) return Zero;
            var coefficients = new int[degree + 1];
            coefficients[0] = coefficient;
            return new Polynomial(this, coefficients);
        }
    }

    private sealed class Polynomial
    {
        private readonly GaloisField field;
        private readonly int[] coefficients;
        public int Degree => coefficients.Length - 1;
        public bool IsZero => coefficients[0] == 0;
        public int LeadingCoefficient => coefficients[0];

        public Polynomial(GaloisField field, int[] source)
        {
            this.field = field;
            int first = 0;
            while (first < source.Length - 1 && source[first] == 0) first++;
            coefficients = source[first..];
        }

        public int GetCoefficient(int degree) => coefficients[coefficients.Length - 1 - degree];
        public int EvaluateAt(int value)
        {
            if (value == 0) return GetCoefficient(0);
            int result = coefficients[0];
            for (int index = 1; index < coefficients.Length; index++)
                result = field.Multiply(result, value) ^ coefficients[index];
            return result;
        }

        public Polynomial AddOrSubtract(Polynomial other)
        {
            if (IsZero) return other;
            if (other.IsZero) return this;
            int[] small = coefficients, large = other.coefficients;
            if (small.Length > large.Length) (small, large) = (large, small);
            var sum = (int[])large.Clone();
            int difference = large.Length - small.Length;
            for (int index = 0; index < small.Length; index++) sum[index + difference] ^= small[index];
            return new Polynomial(field, sum);
        }

        public Polynomial Multiply(Polynomial other)
        {
            if (IsZero || other.IsZero) return field.Zero;
            var product = new int[coefficients.Length + other.coefficients.Length - 1];
            for (int left = 0; left < coefficients.Length; left++)
            for (int right = 0; right < other.coefficients.Length; right++)
                product[left + right] ^= field.Multiply(coefficients[left], other.coefficients[right]);
            return new Polynomial(field, product);
        }

        public Polynomial Multiply(int scalar)
        {
            if (scalar == 0) return field.Zero;
            if (scalar == 1) return this;
            var product = new int[coefficients.Length];
            for (int index = 0; index < product.Length; index++) product[index] = field.Multiply(coefficients[index], scalar);
            return new Polynomial(field, product);
        }

        public Polynomial MultiplyByMonomial(int degree, int coefficient)
        {
            if (coefficient == 0) return field.Zero;
            var product = new int[coefficients.Length + degree];
            for (int index = 0; index < coefficients.Length; index++) product[index] = field.Multiply(coefficients[index], coefficient);
            return new Polynomial(field, product);
        }
    }
}
