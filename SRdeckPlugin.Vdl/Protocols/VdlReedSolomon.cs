namespace SRdeckPlugin.Vdl.Protocols;

/// <summary>VDL Mode 2 RS(255,249) codec over GF(256), p(x)=0x187, first root 120.</summary>
internal static class VdlReedSolomon
{
    internal const int DataSymbols = 249;
    internal const int CodewordSymbols = 255;
    internal const int ParitySymbols = CodewordSymbols - DataSymbols;
    private const int FirstRoot = 120;
    private const int FieldOrder = 255;
    private static readonly byte[] Exponents = new byte[FieldOrder * 2];
    private static readonly int[] Logarithms = new int[256];
    private static readonly byte[] Generator;

    static VdlReedSolomon()
    {
        int value = 1;
        for (int exponent = 0; exponent < FieldOrder; exponent++)
        {
            Exponents[exponent] = (byte)value;
            Logarithms[value] = exponent;
            value <<= 1;
            if ((value & 0x100) != 0) value ^= 0x187;
        }
        for (int exponent = FieldOrder; exponent < Exponents.Length; exponent++)
            Exponents[exponent] = Exponents[exponent - FieldOrder];

        Generator = [1];
        for (int root = 0; root < ParitySymbols; root++)
            Generator = MultiplyPolynomials(Generator, [1, Alpha(FirstRoot + root)]);
    }

    /// <summary>
    /// Corrects a full 255-symbol vector. Only <paramref name="fecSymbols"/> leading parity
    /// symbols were transmitted; the remaining parity positions are treated as erasures.
    /// </summary>
    internal static bool TryDecode(byte[] codeword, int dataLength, int fecSymbols,
        out int correctedSymbols)
    {
        correctedSymbols = 0;
        if (codeword.Length != CodewordSymbols || dataLength is < 0 or > DataSymbols ||
            fecSymbols is < 0 or > ParitySymbols) return false;
        if (fecSymbols == 0) return true;

        byte[] syndromes = CalculateSyndromes(codeword);
        int erasureCount = ParitySymbols - fecSymbols;
        int[] erasures = Enumerable.Range(DataSymbols + fecSymbols, erasureCount).ToArray();
        if (syndromes.All(value => value == 0)) return true;

        if (erasureCount == 0)
        {
            if (!TryFindErrorPositions(syndromes, out int[] errorPositions) ||
                errorPositions.Length > ParitySymbols / 2 ||
                errorPositions.Any(position => position >= dataLength && position < DataSymbols) ||
                !TryCorrectAtPositions(codeword, syndromes, errorPositions, out byte[]? corrected) ||
                corrected is null) return false;
            corrected.CopyTo(codeword, 0);
            correctedSymbols = errorPositions.Length;
            return true;
        }

        var candidates = new List<int>(dataLength + fecSymbols);
        for (int index = 0; index < dataLength; index++) candidates.Add(index);
        for (int index = 0; index < fecSymbols; index++) candidates.Add(DataSymbols + index);
        int maximumErrors = (ParitySymbols - erasureCount) / 2;

        for (int errorCount = 0; errorCount <= maximumErrors; errorCount++)
        {
            int[] selected = new int[errorCount];
            if (TryCombinations(codeword, syndromes, erasures, candidates, selected, 0, 0,
                    out byte[]? corrected) && corrected is not null)
            {
                corrected.CopyTo(codeword, 0);
                correctedSymbols = errorCount;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Retries decoding with known unreliable transmitted symbols treated as erasures.
    /// The untransmitted parity symbols remain erasures as well.
    /// </summary>
    internal static bool TryDecodeWithErasures(byte[] codeword, int dataLength, int fecSymbols,
        ReadOnlySpan<int> unreliablePositions, out int correctedSymbols)
    {
        correctedSymbols = 0;
        if (codeword.Length != CodewordSymbols || dataLength is < 0 or > DataSymbols ||
            fecSymbols is < 1 or > ParitySymbols || unreliablePositions.Length > fecSymbols)
            return false;

        int[] unreliable = unreliablePositions.ToArray();
        if (unreliable.Distinct().Count() != unreliable.Length || unreliable.Any(position =>
                position < 0 ||
                (position >= dataLength && position < DataSymbols) ||
                position >= DataSymbols + fecSymbols)) return false;

        byte[] syndromes = CalculateSyndromes(codeword);
        if (syndromes.All(value => value == 0)) return true;
        int[] missingParity = Enumerable.Range(DataSymbols + fecSymbols,
            ParitySymbols - fecSymbols).ToArray();
        int[] erasures = [.. missingParity, .. unreliable];
        if (erasures.Length > ParitySymbols ||
            !TryCorrectAtPositions(codeword, syndromes, erasures, out byte[]? corrected) ||
            corrected is null) return false;

        correctedSymbols = unreliable.Count(position => corrected[position] != codeword[position]);
        corrected.CopyTo(codeword, 0);
        return true;
    }

    private static bool TryFindErrorPositions(ReadOnlySpan<byte> syndromes, out int[] positions)
    {
        var locator = new byte[ParitySymbols + 1];
        var previous = new byte[ParitySymbols + 1];
        locator[0] = previous[0] = 1;
        int degree = 0;
        int shift = 1;
        byte previousDiscrepancy = 1;
        for (int index = 0; index < ParitySymbols; index++)
        {
            byte discrepancy = syndromes[index];
            for (int term = 1; term <= degree; term++)
                discrepancy ^= Multiply(locator[term], syndromes[index - term]);
            if (discrepancy == 0) { shift++; continue; }
            byte[] saved = (byte[])locator.Clone();
            byte scale = Multiply(discrepancy, Inverse(previousDiscrepancy));
            for (int term = 0; term + shift < locator.Length; term++)
                locator[term + shift] ^= Multiply(scale, previous[term]);
            if (2 * degree <= index)
            {
                degree = index + 1 - degree;
                previous = saved;
                previousDiscrepancy = discrepancy;
                shift = 1;
            }
            else shift++;
        }
        if (degree == 0 || degree > ParitySymbols / 2)
        {
            positions = [];
            return false;
        }
        var found = new List<int>(degree);
        for (int exponent = 0; exponent < FieldOrder; exponent++)
        {
            byte x = Alpha(-exponent);
            byte value = locator[degree];
            for (int term = degree - 1; term >= 0; term--)
                value = (byte)(Multiply(value, x) ^ locator[term]);
            if (value == 0) found.Add(CodewordSymbols - 1 - exponent);
        }
        positions = found.ToArray();
        return positions.Length == degree;
    }

    internal static byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length > DataSymbols) throw new ArgumentOutOfRangeException(nameof(data));
        var codeword = new byte[CodewordSymbols];
        data.CopyTo(codeword);
        var work = (byte[])codeword.Clone();
        for (int index = 0; index < DataSymbols; index++)
        {
            byte coefficient = work[index];
            if (coefficient == 0) continue;
            for (int term = 1; term < Generator.Length; term++)
                work[index + term] ^= Multiply(Generator[term], coefficient);
        }
        Array.Copy(work, DataSymbols, codeword, DataSymbols, ParitySymbols);
        return codeword;
    }

    private static bool TryCombinations(byte[] received, byte[] syndromes, int[] erasures,
        IReadOnlyList<int> candidates, int[] selected, int depth, int start,
        out byte[]? corrected)
    {
        if (depth == selected.Length)
        {
            int[] positions = [.. erasures, .. selected];
            if (TryCorrectAtPositions(received, syndromes, positions, out corrected)) return true;
            corrected = null;
            return false;
        }
        int remaining = selected.Length - depth;
        for (int index = start; index <= candidates.Count - remaining; index++)
        {
            selected[depth] = candidates[index];
            if (TryCombinations(received, syndromes, erasures, candidates, selected,
                    depth + 1, index + 1, out corrected)) return true;
        }
        corrected = null;
        return false;
    }

    private static bool TryCorrectAtPositions(byte[] received, byte[] syndromes,
        IReadOnlyList<int> positions, out byte[]? corrected)
    {
        corrected = null;
        int count = positions.Count;
        if (count == 0) return syndromes.All(value => value == 0);
        var matrix = new byte[count, count + 1];
        for (int row = 0; row < count; row++)
        {
            int root = FirstRoot + row;
            for (int column = 0; column < count; column++)
                matrix[row, column] = Alpha(root * (CodewordSymbols - 1 - positions[column]));
            matrix[row, count] = syndromes[row];
        }
        if (!TrySolve(matrix, count, out byte[] magnitudes)) return false;

        for (int row = count; row < ParitySymbols; row++)
        {
            int root = FirstRoot + row;
            byte predicted = 0;
            for (int column = 0; column < count; column++)
                predicted ^= Multiply(magnitudes[column],
                    Alpha(root * (CodewordSymbols - 1 - positions[column])));
            if (predicted != syndromes[row]) return false;
        }

        var candidate = (byte[])received.Clone();
        for (int index = 0; index < count; index++) candidate[positions[index]] ^= magnitudes[index];
        corrected = candidate;
        return true;
    }

    private static bool TrySolve(byte[,] matrix, int size, out byte[] solution)
    {
        solution = new byte[size];
        for (int pivot = 0; pivot < size; pivot++)
        {
            int row = pivot;
            while (row < size && matrix[row, pivot] == 0) row++;
            if (row == size) return false;
            if (row != pivot)
                for (int column = pivot; column <= size; column++)
                    (matrix[pivot, column], matrix[row, column]) = (matrix[row, column], matrix[pivot, column]);
            byte inverse = Inverse(matrix[pivot, pivot]);
            for (int column = pivot; column <= size; column++)
                matrix[pivot, column] = Multiply(matrix[pivot, column], inverse);
            for (row = 0; row < size; row++)
            {
                if (row == pivot || matrix[row, pivot] == 0) continue;
                byte factor = matrix[row, pivot];
                for (int column = pivot; column <= size; column++)
                    matrix[row, column] ^= Multiply(factor, matrix[pivot, column]);
            }
        }
        for (int row = 0; row < size; row++) solution[row] = matrix[row, size];
        return true;
    }

    private static byte[] CalculateSyndromes(ReadOnlySpan<byte> codeword)
    {
        var result = new byte[ParitySymbols];
        for (int syndrome = 0; syndrome < result.Length; syndrome++)
        {
            byte root = Alpha(FirstRoot + syndrome);
            byte value = 0;
            foreach (byte symbol in codeword) value = (byte)(Multiply(value, root) ^ symbol);
            result[syndrome] = value;
        }
        return result;
    }

    private static byte[] MultiplyPolynomials(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length - 1];
        for (int i = 0; i < left.Length; i++)
            for (int j = 0; j < right.Length; j++)
                result[i + j] ^= Multiply(left[i], right[j]);
        return result;
    }

    private static byte Alpha(int exponent)
    {
        exponent %= FieldOrder;
        if (exponent < 0) exponent += FieldOrder;
        return Exponents[exponent];
    }

    private static byte Multiply(byte left, byte right) => left == 0 || right == 0
        ? (byte)0 : Exponents[Logarithms[left] + Logarithms[right]];

    private static byte Inverse(byte value)
    {
        if (value == 0) throw new DivideByZeroException();
        return Exponents[FieldOrder - Logarithms[value]];
    }
}
