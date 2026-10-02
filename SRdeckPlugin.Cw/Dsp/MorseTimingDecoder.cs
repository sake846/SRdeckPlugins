using System.Text;
using SRdeckPlugin.Cw.Models;

namespace SRdeckPlugin.Cw.Dsp;

/// <summary>
/// Converts a sampled key-down/key-up stream to Morse text while continuously
/// adapting its dot duration. Calls are expected at a fixed cadence.
/// </summary>
public sealed class MorseTimingDecoder
{
    private const double MinimumUnitMilliseconds = 25;
    private const double MaximumUnitMilliseconds = 240;
    private const double DashThresholdUnits = 1.7;
    private const int MaximumTextLength = 96;
    private static readonly double LogThree = Math.Log(3.0);
    private static readonly double MinimumLogScale = Math.Log(0.7);
    private static readonly double MaximumLogScale = Math.Log(1.4);

    private static readonly IReadOnlyDictionary<string, char> WesternAlphabet =
        new Dictionary<string, char>(StringComparer.Ordinal)
        {
            [".-"] = 'A', ["-..."] = 'B', ["-.-."] = 'C', ["-.."] = 'D',
            ["."] = 'E', ["..-."] = 'F', ["--."] = 'G', ["...."] = 'H',
            [".."] = 'I', [".---"] = 'J', ["-.-"] = 'K', [".-.."] = 'L',
            ["--"] = 'M', ["-."] = 'N', ["---"] = 'O', [".--."] = 'P',
            ["--.-"] = 'Q', [".-."] = 'R', ["..."] = 'S', ["-"] = 'T',
            ["..-"] = 'U', ["...-"] = 'V', [".--"] = 'W', ["-..-"] = 'X',
            ["-.--"] = 'Y', ["--.."] = 'Z',
            ["-----"] = '0', [".----"] = '1', ["..---"] = '2',
            ["...--"] = '3', ["....-"] = '4', ["....."] = '5',
            ["-...."] = '6', ["--..."] = '7', ["---.."] = '8',
            ["----."] = '9', ["-..-."] = '/', [".-.-.-"] = '.',
            ["--..--"] = ',', ["..--.."] = '?', ["-....-"] = '-',
            [".-.-."] = '+', ["-...-"] = '=', [".----."] = '\'',
            ["-.-.--"] = '!', ["-.--."] = '(', ["-.--.-"] = ')',
            [".-..."] = '&', ["---..."] = ':', ["-.-.-."] = ';',
            [".-..-."] = '"', ["..--.-"] = '_', [".--.-."] = '@'
        };

    private static readonly IReadOnlyDictionary<string, char> WabunAlphabet =
        new Dictionary<string, char>(StringComparer.Ordinal)
        {
            [".-"] = 'イ', [".-.-"] = 'ロ', ["-..."] = 'ハ', ["-.-."] = 'ニ',
            ["-.."] = 'ホ', ["."] = 'ヘ', ["..-.."] = 'ト', ["..-."] = 'チ',
            ["--."] = 'リ', ["...."] = 'ヌ', ["-.-.."] = 'ル', ["-.---"] = 'ヲ',
            [".---"] = 'ワ', ["-.-"] = 'カ', [".-.."] = 'ヨ', ["--"] = 'タ',
            ["-."] = 'レ', ["---"] = 'ソ', [".--."] = 'ツ', ["--.-"] = 'ネ',
            [".-."] = 'ナ', ["..."] = 'ラ', ["-"] = 'ム', ["..-"] = 'ウ',
            [".-..."] = 'ヰ', ["...-"] = 'ノ', [".--"] = 'オ', ["-..-"] = 'ク',
            ["-.--"] = 'ヤ', ["--.."] = 'マ', ["....-"] = 'ケ', ["-.-.-"] = 'フ',
            ["-..-."] = 'コ', ["-...."] = 'エ', ["--..."] = 'テ', ["---.."] = 'ア',
            ["----."] = 'サ', ["--..--"] = 'キ', ["..--.."] = 'ユ', ["-....-"] = 'メ',
            ["..-.-"] = 'ミ', ["---.-"] = 'シ', [".-.-."] = 'ヱ', ["-...-"] = 'ヒ',
            ["--.-."] = 'モ', [".----"] = 'セ', ["---..."] = 'ス', [".-.-.-"] = 'ン',
            [".."] = '゛', ["..--."] = '゜', [".-.-.."] = '¶',
            // Several Wabun kana use the same code points as International
            // Morse digits (for example セ/1 and ケ/4).  A Dictionary cannot
            // contain both and, more importantly, the receiver has no numeric
            // shift state with which to disambiguate them.  Prefer kana while
            // in Wabun mode; operators can switch back to Western mode with
            // the ラタ prosign before sending International Morse digits.
            ["-----"] = '0', ["..---"] = '2', ["...--"] = '3', ["....."] = '5'
        };

    private static readonly Candidate[][] WesternCandidatesByLength = CreateCandidatesByLength(WesternAlphabet);
    private static readonly Candidate[][] WabunCandidatesByLength = CreateCandidatesByLength(WabunAlphabet);

    private readonly StringBuilder symbols = new(8);
    private readonly List<double> markDurations = new(8);
    private readonly StringBuilder text = new(MaximumTextLength);
    private readonly double[] logMarkDurations = new double[8];
    private bool initialized;
    private bool keyDown;
    private bool characterEmittedForGap;
    private bool wordEmittedForGap;
    private bool hasTimingObservation;
    private bool hasDotObservation;
    private double runMilliseconds;
    private double unitMilliseconds = 60;

    public CwLanguage CurrentLanguage { get; set; } = CwLanguage.Western;

    public string Text => text.ToString();
    public int TextLength => text.Length;
    public bool HasText => text.Length != 0;
    public int SpeedWpm => (int)Math.Round(1_200 / unitMilliseconds);
    public bool IsKeyDown => keyDown;
    public double CurrentSilenceMilliseconds => initialized && !keyDown ? runMilliseconds : 0;
    public double LastCharacterConfidence { get; private set; } = 1;

    /// <summary>Processes one state interval and returns newly emitted text.</summary>
    public string Process(bool isKeyDown, double elapsedMilliseconds)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));

        if (!initialized)
        {
            initialized = true;
            keyDown = isKeyDown;
            runMilliseconds = elapsedMilliseconds;
            return string.Empty;
        }

        if (isKeyDown == keyDown)
        {
            runMilliseconds += elapsedMilliseconds;
            return keyDown ? string.Empty : EmitExpiredGap();
        }

        string appended;
        if (keyDown)
        {
            EncodeMark(runMilliseconds);
            appended = string.Empty;
            characterEmittedForGap = false;
            wordEmittedForGap = false;
        }
        else
        {
            appended = EmitGap(runMilliseconds);
        }

        keyDown = isKeyDown;
        runMilliseconds = elapsedMilliseconds;
        return appended;
    }

    public string Flush()
    {
        if (initialized && keyDown) EncodeMark(runMilliseconds);
        string appended = FinishCharacter();
        initialized = false;
        keyDown = false;
        runMilliseconds = 0;
        characterEmittedForGap = false;
        wordEmittedForGap = false;
        hasTimingObservation = false;
        hasDotObservation = false;
        LastCharacterConfidence = 1;
        return appended;
    }

    public void Reset()
    {
        symbols.Clear();
        markDurations.Clear();
        text.Clear();
        initialized = false;
        keyDown = false;
        runMilliseconds = 0;
        unitMilliseconds = 60;
        characterEmittedForGap = false;
        wordEmittedForGap = false;
        hasTimingObservation = false;
        hasDotObservation = false;
    }

    private void EncodeMark(double durationMilliseconds)
    {
        // State confirmation already rejects one-frame impulses. Keep a small
        // guard for a track created near the end of a mark without discarding
        // valid high-speed dots.
        if (durationMilliseconds < Math.Max(8, unitMilliseconds * 0.25)) return;
        bool dash = durationMilliseconds >= unitMilliseconds * DashThresholdUnits;
        if (symbols.Length < 8)
        {
            symbols.Append(dash ? '-' : '.');
            markDurations.Add(durationMilliseconds);
        }

        double observation = dash ? durationMilliseconds / 3.0 : durationMilliseconds;
        if (!dash && !hasDotObservation)
        {
            // Dots are the least ambiguous timing reference. Let the first
            // complete dot acquire the sender's speed immediately.
            UpdateUnit(observation, 1.0);
            hasDotObservation = true;
            hasTimingObservation = true;
        }
        else
        {
            // A first dash is ambiguous (it may be a slow dot), and operators
            // commonly shorten dashes. Give dash-derived estimates less weight.
            double weight = dash
                ? (hasTimingObservation ? 0.08 : 0.25)
                : 0.22;
            UpdateUnit(observation, weight);
            hasTimingObservation = true;
            if (!dash) hasDotObservation = true;
        }
    }

    private string EmitExpiredGap()
    {
        string appended = string.Empty;
        if (!characterEmittedForGap && runMilliseconds >= unitMilliseconds * 1.8)
        {
            appended = FinishCharacter();
            characterEmittedForGap = true;
        }
        if (!wordEmittedForGap && runMilliseconds >= unitMilliseconds * 5.5 &&
            text.Length > 0 && text[^1] != ' ')
        {
            AppendText(" ");
            wordEmittedForGap = true;
            return appended.Length == 0 ? " " : appended + " ";
        }
        return appended;
    }

    private string EmitGap(double durationMilliseconds)
    {
        string appended = string.Empty;
        ObserveCompletedGap(durationMilliseconds);
        if (durationMilliseconds >= unitMilliseconds * 1.8)
        {
            appended = FinishCharacter();
            characterEmittedForGap = true;
        }
        if (durationMilliseconds >= unitMilliseconds * 5.5 &&
            text.Length > 0 && text[^1] != ' ')
        {
            AppendText(" ");
            wordEmittedForGap = true;
            return appended.Length == 0 ? " " : appended + " ";
        }
        return appended;
    }

    private void ObserveCompletedGap(double durationMilliseconds)
    {
        // Inter-element gaps are useful speed observations. Character gaps are
        // weighted lightly and word gaps only very lightly because Farnsworth
        // spacing intentionally stretches them.
        if (durationMilliseconds >= unitMilliseconds * 5.5)
            UpdateUnit(durationMilliseconds / 7.0, 0.03);
        else if (durationMilliseconds >= unitMilliseconds * 1.8)
            UpdateUnit(durationMilliseconds / 3.0, 0.08);
        else if (durationMilliseconds >= unitMilliseconds * 0.45)
            UpdateUnit(durationMilliseconds, 0.12);
    }

    private void UpdateUnit(double observationMilliseconds, double weight)
    {
        observationMilliseconds = Math.Clamp(
            observationMilliseconds, MinimumUnitMilliseconds, MaximumUnitMilliseconds);
        weight = Math.Clamp(weight, 0, 1);
        unitMilliseconds = Math.Clamp(
            unitMilliseconds * (1 - weight) + observationMilliseconds * weight,
            MinimumUnitMilliseconds, MaximumUnitMilliseconds);
    }

    private string FinishCharacter()
    {
        if (symbols.Length == 0) return string.Empty;
        string code = symbols.ToString();
        symbols.Clear();
        
        if (code == "-..---")
        {
            CurrentLanguage = CwLanguage.Wabun;
            markDurations.Clear();
            string marker = "<和>";
            AppendText(marker);
            return marker;
        }
        if (code == "...-.")
        {
            CurrentLanguage = CwLanguage.Western;
            markDurations.Clear();
            string marker = "<欧>";
            AppendText(marker);
            return marker;
        }

        char decoded = DecodeBestCharacter(code);
        markDurations.Clear();
        string appended = decoded.ToString();
        AppendText(appended);
        return appended;
    }

    private char DecodeBestCharacter(string hardCode)
    {
        var alphabet = CurrentLanguage == CwLanguage.Wabun ? WabunAlphabet : WesternAlphabet;
        var candidatesByLength = CurrentLanguage == CwLanguage.Wabun ? WabunCandidatesByLength : WesternCandidatesByLength;

        if (markDurations.Count != hardCode.Length)
        {
            LastCharacterConfidence = alphabet.ContainsKey(hardCode) ? 0.6 : 0;
            return alphabet.TryGetValue(hardCode, out char hardValue) ? hardValue : '?';
        }

        Candidate? bestCandidate = null;
        char bestCharacter = '?';
        double bestScore = double.PositiveInfinity;
        int length = markDurations.Count;
        if ((uint)length >= (uint)candidatesByLength.Length)
        {
            LastCharacterConfidence = alphabet.ContainsKey(hardCode) ? 0.6 : 0;
            return alphabet.TryGetValue(hardCode, out char hardValue) ? hardValue : '?';
        }
        double logUnit = Math.Log(unitMilliseconds);
        double logMarkSum = 0;
        for (int index = 0; index < length; index++)
        {
            double value = Math.Log(markDurations[index]) - logUnit;
            logMarkDurations[index] = value;
            logMarkSum += value;
        }
        foreach (Candidate candidate in candidatesByLength[length])
        {
            double logScale = Math.Clamp(
                (logMarkSum - candidate.DashCount * LogThree) / length,
                MinimumLogScale, MaximumLogScale);

            double score = 0.15 * logScale * logScale;
            for (int index = 0; index < length; index++)
            {
                double error = logMarkDurations[index] -
                    (candidate.Code[index] == '-' ? LogThree : 0) - logScale;
                score += error * error / length;
            }
            if (score >= bestScore) continue;
            bestScore = score;
            bestCandidate = candidate;
            bestCharacter = candidate.Character;
        }

        LastCharacterConfidence = bestCandidate is null ? 0 :
            Math.Clamp(1 - bestScore / 0.45, 0, 1);
        if (bestCandidate is null || bestScore > 0.45)
            return alphabet.TryGetValue(hardCode, out char hardValue) ? hardValue : '?';

        // Feed the soft decision back into the speed estimator. Dots remain the
        // strongest observations; dash timing varies more between operators.
        for (int index = 0; index < bestCandidate.Value.Code.Length; index++)
        {
            bool dash = bestCandidate.Value.Code[index] == '-';
            UpdateUnit(markDurations[index] / (dash ? 3.0 : 1.0), dash ? 0.03 : 0.10);
            if (!dash) hasDotObservation = true;
        }
        return bestCharacter;
    }

    private static Candidate[][] CreateCandidatesByLength(IReadOnlyDictionary<string, char> alphabet)
    {
        var buckets = new List<Candidate>[9];
        for (int index = 0; index < buckets.Length; index++) buckets[index] = [];
        foreach ((string code, char character) in alphabet)
        {
            int dashCount = 0;
            foreach (char symbol in code)
                if (symbol == '-') dashCount++;
            buckets[code.Length].Add(new Candidate(code, character, dashCount));
        }
        var result = new Candidate[buckets.Length][];
        for (int index = 0; index < result.Length; index++)
            result[index] = buckets[index].ToArray();
        return result;
    }

    private void AppendText(string value)
    {
        text.Append(value);
        if (text.Length > MaximumTextLength)
            text.Remove(0, text.Length - MaximumTextLength);
    }

    private readonly record struct Candidate(string Code, char Character, int DashCount);
}
