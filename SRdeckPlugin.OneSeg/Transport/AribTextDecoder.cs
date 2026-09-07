using System.Text;

namespace SRdeckPlugin.OneSeg.Transport;

internal static class AribTextDecoder
{
    private enum Set { Kanji, Alphanumeric, Hiragana, Katakana, AdditionalSymbols, Drcs2, Drcs, Unsupported }
    private static readonly Encoding Japanese;

    static AribTextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Japanese = Encoding.GetEncoding(50220, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public static string Decode(ReadOnlySpan<byte> source) => Decode(source, 0, null);

    public static string DecodeSi(ReadOnlySpan<byte> source) => Decode(source, 2, null);

    internal static string Decode(ReadOnlySpan<byte> source, AribDrcsStore drcs)
        => Decode(source, 0, drcs);

    private static string Decode(ReadOnlySpan<byte> source, int initialGr, AribDrcsStore? drcs)
    {
        var output = new StringBuilder(source.Length);
        Set[] g = [Set.Kanji, Set.Alphanumeric, Set.Hiragana, Set.Katakana];
        int[] drcsSets = [0, 0, 0, 0];
        // One-seg captions invoke the two-byte Kanji set in both GL and GR at
        // statement start.  GR bytes therefore carry JIS pairs with bit 7 set.
        int gl = 0, gr = initialGr;
        int singleShift = -1;
        for (int index = 0; index < source.Length; index++)
        {
            byte value = source[index];
            if (value == 0x0f) { gl = 0; continue; }
            if (value == 0x0e) { gl = 1; continue; }
            if (value == 0x19) { singleShift = 2; continue; }
            if (value == 0x1d) { singleShift = 3; continue; }
            if (value == 0x1b)
            {
                ParseEscape(source, ref index, g, drcsSets, ref gl, ref gr);
                continue;
            }
            if (value is 0x0a or 0x0d) { output.AppendLine(); continue; }
            if (value is 0x20 or 0xa0) { output.Append(' '); continue; }
            if (value < 0x20 || value is >= 0x7f and <= 0xa0)
            {
                SkipControl(source, ref index, value);
                continue;
            }
            bool right = value >= 0xa1;
            if ((!right && value > 0x7e) || (right && value > 0xfe)) continue;
            int setIndex = singleShift >= 0 ? singleShift : right ? gr : gl;
            singleShift = -1;
            Set set = g[setIndex];
            byte code = (byte)(value & 0x7f);
            if (set == Set.Kanji)
            {
                if (++index >= source.Length) break;
                byte second = source[index];
                if ((!right && second is < 0x21 or > 0x7e) ||
                    (right && second is < 0xa1 or > 0xfe))
                {
                    // Do not consume a control byte (or the first byte of a
                    // recovery sequence) as the second half of a bad pair.
                    index--;
                    continue;
                }
                output.Append(DecodeKanji(code, (byte)(second & 0x7f)));
            }
            else if (set == Set.AdditionalSymbols)
            {
                if (++index >= source.Length) break;
                byte secondValue = source[index];
                if ((!right && secondValue is < 0x21 or > 0x7e) ||
                    (right && secondValue is < 0xa1 or > 0xfe))
                {
                    // A damaged/incomplete two-byte character must not eat the
                    // following control or character.  This is common at the
                    // end of a live caption statement when the PES is split.
                    index--;
                    continue;
                }
                byte second = (byte)(secondValue & 0x7f);
                output.Append(DecodeAdditionalSymbol(code, second));
            }
            else if (set == Set.Alphanumeric)
            {
                output.Append(code == 0x5c ? '¥' : (char)code);
            }
            else if (set is Set.Hiragana or Set.Katakana)
            {
                output.Append(DecodeKana(set, code));
            }
            else if (set == Set.Drcs2)
            {
                if (++index >= source.Length) break;
                byte secondValue = source[index];
                if ((!right && secondValue is < 0x21 or > 0x7e) ||
                    (right && secondValue is < 0xa1 or > 0xfe))
                {
                    index--;
                    continue;
                }

                int drcsCode = (code << 8) | (secondValue & 0x7f);
                output.Append(DecodeDrcs(drcs, drcsSets[setIndex], drcsCode));
            }
            else if (set == Set.Drcs)
            {
                output.Append(DecodeDrcs(drcs, drcsSets[setIndex], code));
            }
        }
        return string.Join('\n', output.ToString().Split(['\r','\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }

    private static void ParseEscape(ReadOnlySpan<byte> source, ref int index, Set[] g, int[] drcsSets,
        ref int gl, ref int gr)
    {
        if (index + 1 >= source.Length) return;
        byte next = source[index + 1];
        if (next is >= 0x6e and <= 0x6f) { gl = next - 0x6c; index++; return; }
        if (next is >= 0x7c and <= 0x7e) { gr = 0x7f - next; index++; return; }
        if (next == 0x24)
        {
            if (index + 2 >= source.Length) return;
            byte selector = source[index + 2];
            if (selector is >= 0x28 and <= 0x2b)
            {
                int target = selector - 0x28;
                if (index + 3 >= source.Length) { index += 2; return; }
                byte final = source[index + 3];
                if (final == 0x20 && index + 4 < source.Length)
                {
                    g[target] = Set.Drcs2;
                    drcsSets[target] = 0;
                    index += 4;
                }
                else
                {
                    g[target] = DesignateTwoByte(final, out drcsSets[target]);
                    index += 3;
                }
            }
            else
            {
                g[0] = DesignateTwoByte(selector, out drcsSets[0]);
                index += 2;
            }
            return;
        }
        if (next is >= 0x28 and <= 0x2b)
        {
            int target = next - 0x28;
            if (index + 2 >= source.Length) { index++; return; }
            byte final = source[index + 2];
            if (final == 0x20 && index + 3 < source.Length)
            {
                byte drcsFinal = source[index + 3];
                g[target] = Set.Drcs;
                drcsSets[target] = Math.Clamp(drcsFinal - 0x40, 1, 15);
                index += 3;
            }
            else
            {
                g[target] = Designate(final);
                index += 2;
            }
        }
    }

    private static void SkipControl(ReadOnlySpan<byte> source, ref int index, byte value)
    {
        if (value == 0x9b)
        {
            while (index + 1 < source.Length)
            {
                byte next = source[++index];
                if (next is >= 0x40 and <= 0x7e) break;
            }
            return;
        }
        int parameters = value switch
        {
            0x16 or 0x8b or 0x91 or 0x92 or 0x93 or 0x94 or 0x95 or 0x97 or 0x98 => 1,
            0x1c => 2,
            0x90 when index + 1 < source.Length && source[index + 1] == 0x20 => 2,
            0x90 => 1,
            _ => 0
        };
        index = Math.Min(source.Length - 1, index + parameters);
    }

    private static Set Designate(byte final) => final switch
    {
        0x4a or 0x42 => Set.Alphanumeric,
        0x30 => Set.Hiragana,
        0x31 or 0x49 => Set.Katakana,
        _ => Set.Unsupported
    };

    private static Set DesignateTwoByte(byte final, out int drcsSet)
    {
        drcsSet = final is >= 0x40 and <= 0x4f ? final - 0x40 : 0;
        return final switch
        {
            0x39 or 0x42 => Set.Kanji,
            0x3b => Set.AdditionalSymbols,
            >= 0x40 and <= 0x4f => Set.Drcs2,
            _ => Set.Unsupported
        };
    }

    private static string DecodeAdditionalSymbol(byte first, byte second)
    {
        int code = (first << 8) | second;
        return AdditionalSymbols.TryGetValue(code, out string? value) ? value : "〓";
    }

    // ARIB STD-B24 additional symbols and the fixed external-character table used
    // by SI.  A short event descriptor can designate this set with ESC $ 3B;
    // treating the complete table as unknown text is what used to turn common
    // names in EIT titles into a geta mark (\u3007).
    private static readonly IReadOnlyDictionary<int, string> AdditionalSymbols = new Dictionary<int, string>
    {
        [0x7a50]="[HV]", [0x7a51]="[SD]", [0x7a52]="[Ｐ]", [0x7a53]="[Ｗ]",
        [0x7a54]="[MV]", [0x7a55]="[手]", [0x7a56]="[字]", [0x7a57]="[双]",
        [0x7a58]="[デ]", [0x7a59]="[Ｓ]", [0x7a5a]="[二]", [0x7a5b]="[多]",
        [0x7a5c]="[解]", [0x7a5d]="[SS]", [0x7a5e]="[Ｂ]", [0x7a5f]="[Ｎ]",
        [0x7a60]="■", [0x7a61]="●", [0x7a62]="[天]", [0x7a63]="[交]",
        [0x7a64]="[映]", [0x7a65]="[無]", [0x7a66]="[料]", [0x7a67]="[年齢制限]",
        [0x7a68]="[前]", [0x7a69]="[後]", [0x7a6a]="[再]", [0x7a6b]="[新]",
        [0x7a6c]="[初]", [0x7a6d]="[終]", [0x7a6e]="[生]", [0x7a6f]="[販]",
        [0x7a70]="[声]", [0x7a71]="[吹]", [0x7a72]="[PPV]", [0x7a73]="㊙",
        [0x7a74]="\U0001f200", [0x7c21]="→", [0x7c22]="←", [0x7c23]="↑",
        [0x7c24]="↓", [0x7c25]="●", [0x7c26]="○", [0x7c27]="年",
        [0x7c28]="月", [0x7c29]="日", [0x7c2a]="円", [0x7c2b]="㎡",
        [0x7c2c]="㎥", [0x7c2d]="㎝", [0x7c2e]="㎠", [0x7c2f]="㎤",
        [0x7c30]="０.", [0x7c31]="１.", [0x7c32]="２.", [0x7c33]="３.",
        [0x7c34]="４.", [0x7c35]="５.", [0x7c36]="６.", [0x7c37]="７.",
        [0x7c38]="８.", [0x7c39]="９.", [0x7c3a]="氏", [0x7c3b]="副",
        [0x7c3c]="元", [0x7c3d]="故", [0x7c3e]="前", [0x7c3f]="新",
        [0x7c40]="０,", [0x7c41]="１,", [0x7c42]="２,", [0x7c43]="３,",
        [0x7c44]="４,", [0x7c45]="５,", [0x7c46]="６,", [0x7c47]="７,",
        [0x7c48]="８,", [0x7c49]="９,", [0x7c4a]="㈳", [0x7c4b]="㈶",
        [0x7c4c]="㈲", [0x7c4d]="㈱", [0x7c4e]="㈹", [0x7c4f]="㉄",
        [0x7c50]="▶", [0x7c51]="◀", [0x7c52]="〖", [0x7c53]="〗",
        [0x7c54]="⟐", [0x7c55]="^2", [0x7c56]="^3", [0x7c57]="(CD)",
        [0x7c58]="(vn)", [0x7c59]="(ob)", [0x7c5a]="(cb)", [0x7c5b]="(ce",
        [0x7c5c]="mb)", [0x7c5d]="(hp)", [0x7c5e]="(br)", [0x7c5f]="(p)",
        [0x7c60]="(s)", [0x7c61]="(ms)", [0x7c62]="(t)", [0x7c63]="(bs)",
        [0x7c64]="(b)", [0x7c65]="(tb)", [0x7c66]="(tp)", [0x7c67]="(ds)",
        [0x7c68]="(ag)", [0x7c69]="(eg)", [0x7c6a]="(vo)", [0x7c6b]="(fl)",
        [0x7c6c]="(ke", [0x7c6d]="y)", [0x7c6e]="(sa", [0x7c6f]="x)",
        [0x7c70]="(sy", [0x7c71]="n)", [0x7c72]="(or", [0x7c73]="g)",
        [0x7c74]="(pe", [0x7c75]="r)", [0x7c76]="(R)", [0x7c77]="(C)",
        [0x7c78]="(箏)", [0x7c79]="DJ", [0x7c7a]="\U0001f226", [0x7c7b]="Fax",
        [0x7d21]="㈪", [0x7d22]="㈫", [0x7d23]="㈬", [0x7d24]="㈭",
        [0x7d25]="㈮", [0x7d26]="㈯", [0x7d27]="㈰", [0x7d28]="㈷",
        [0x7d29]="㍾", [0x7d2a]="㍽", [0x7d2b]="㍼", [0x7d2c]="㍻",
        [0x7d2d]="№", [0x7d2e]="℡", [0x7d2f]="〶", [0x7d30]="○",
        [0x7d31]="\U0001f240", [0x7d32]="\U0001f241", [0x7d33]="\U0001f242",
        [0x7d34]="\U0001f243", [0x7d35]="\U0001f244", [0x7d36]="\U0001f245",
        [0x7d37]="\U0001f246", [0x7d38]="\U0001f247", [0x7d39]="\U0001f248",
        [0x7d3a]="\U0001f122", [0x7d3b]="\U0001f227", [0x7d3c]="\U0001f228",
        [0x7d3d]="\U0001f229", [0x7d3e]="[二]", [0x7d3f]="\U0001f22a",
        [0x7d40]="\U0001f22b", [0x7d41]="\U0001f22c", [0x7d42]="\U0001f22d",
        [0x7d43]="\U0001f22e", [0x7d44]="\U0001f22f", [0x7d45]="\U0001f230",
        [0x7d46]="\U0001f231", [0x7d47]="㍑", [0x7d48]="㎏", [0x7d49]="㎐",
        [0x7d4a]="ha", [0x7d4b]="㎞", [0x7d4c]="㎢", [0x7d4d]="㍱",
        [0x7d4e]="・", [0x7d4f]="・", [0x7d50]="1/2", [0x7d51]="0/3",
        [0x7d52]="1/3", [0x7d53]="2/3", [0x7d54]="1/4", [0x7d55]="3/4",
        [0x7d56]="1/5", [0x7d57]="2/5", [0x7d58]="3/5", [0x7d59]="4/5",
        [0x7d5a]="1/6", [0x7d5b]="5/6", [0x7d5c]="1/7", [0x7d5d]="1/8",
        [0x7d5e]="1/9", [0x7d5f]="1/10", [0x7d60]="☀", [0x7d61]="☁",
        [0x7d62]="☂", [0x7d63]="☃", [0x7d64]="☖", [0x7d65]="☗",
        [0x7d66]="▽", [0x7d67]="▼", [0x7d68]="♦", [0x7d69]="♥",
        [0x7d6a]="♣", [0x7d6b]="♠", [0x7d6c]="⌺", [0x7d6d]="⦿",
        [0x7d6e]="‼", [0x7d6f]="⁉", [0x7d70]="(曇/晴)", [0x7d71]="☔",
        [0x7d72]="(雨)", [0x7d73]="(雪)", [0x7d74]="(大雪)", [0x7d75]="⚡",
        [0x7d76]="(雷雨)", [0x7d77]="　", [0x7d78]="・", [0x7d79]="・",
        [0x7d7a]="♬", [0x7d7b]="☎", [0x7e21]="Ⅰ", [0x7e22]="Ⅱ",
        [0x7e23]="Ⅲ", [0x7e24]="Ⅳ", [0x7e25]="Ⅴ", [0x7e26]="Ⅵ",
        [0x7e27]="Ⅶ", [0x7e28]="Ⅷ", [0x7e29]="Ⅸ", [0x7e2a]="Ⅹ",
        [0x7e2b]="Ⅺ", [0x7e2c]="Ⅻ", [0x7e2d]="⑰", [0x7e2e]="⑱",
        [0x7e2f]="⑲", [0x7e30]="⑳", [0x7e31]="⑴", [0x7e32]="⑵",
        [0x7e33]="⑶", [0x7e34]="⑷", [0x7e35]="⑸", [0x7e36]="⑹",
        [0x7e37]="⑺", [0x7e38]="⑻", [0x7e39]="⑼", [0x7e3a]="⑽",
        [0x7e3b]="⑾", [0x7e3c]="⑿", [0x7e3d]="㉑", [0x7e3e]="㉒",
        [0x7e3f]="㉓", [0x7e40]="㉔", [0x7e41]="(A)", [0x7e42]="(B)",
        [0x7e43]="(C)", [0x7e44]="(D)", [0x7e45]="(E)", [0x7e46]="(F)",
        [0x7e47]="(G)", [0x7e48]="(H)", [0x7e49]="(I)", [0x7e4a]="(J)",
        [0x7e4b]="(K)", [0x7e4c]="(L)", [0x7e4d]="(M)", [0x7e4e]="(N)",
        [0x7e4f]="(O)", [0x7e50]="(P)", [0x7e51]="(Q)", [0x7e52]="(R)",
        [0x7e53]="(S)", [0x7e54]="(T)", [0x7e55]="(U)", [0x7e56]="(V)",
        [0x7e57]="(W)", [0x7e58]="(X)", [0x7e59]="(Y)", [0x7e5a]="(Z)",
        [0x7e5b]="㉕", [0x7e5c]="㉖", [0x7e5d]="㉗", [0x7e5e]="㉘",
        [0x7e5f]="㉙", [0x7e60]="㉚", [0x7e61]="①", [0x7e62]="②",
        [0x7e63]="③", [0x7e64]="④", [0x7e65]="⑤", [0x7e66]="⑥",
        [0x7e67]="⑦", [0x7e68]="⑧", [0x7e69]="⑨", [0x7e6a]="⑩",
        [0x7e6b]="⑪", [0x7e6c]="⑫", [0x7e6d]="⑬", [0x7e6e]="⑭",
        [0x7e6f]="⑮", [0x7e70]="⑯", [0x7e71]="❶", [0x7e72]="❷",
        [0x7e73]="❸", [0x7e74]="❹", [0x7e75]="❺", [0x7e76]="❻",
        [0x7e77]="❼", [0x7e78]="❽", [0x7e79]="❾", [0x7e7a]="❿",
        [0x7e7b]="⓫", [0x7e7c]="⓬", [0x7e7d]="㉛",

        // ARIB fixed external Kanji used by SI (including human names).
        [0x7521]="㐂", [0x7522]="亭", [0x7523]="份", [0x7524]="仿",
        [0x7525]="侚", [0x7526]="俉", [0x7527]="傜", [0x7528]="儞",
        [0x7529]="冼", [0x752a]="㔟", [0x752b]="匇", [0x752c]="卡",
        [0x752d]="卬", [0x752e]="詹", [0x752f]="吉", [0x7530]="呍",
        [0x7531]="咖", [0x7532]="咜", [0x7533]="咩", [0x7534]="唎",
        [0x7535]="啊", [0x7536]="噲", [0x7537]="囤", [0x7538]="圳",
        [0x7539]="圴", [0x753a]="塚", [0x753b]="墀", [0x753c]="姤",
        [0x753d]="娣", [0x753e]="婕", [0x753f]="寬", [0x7540]="﨑",
        [0x7541]="㟢", [0x7542]="庬", [0x7543]="弴", [0x7544]="彅",
        [0x7545]="德", [0x7546]="怗", [0x7547]="恵", [0x7548]="愰",
        [0x7549]="昤", [0x754a]="曈", [0x754b]="曙", [0x754c]="曺",
        [0x754d]="曻", [0x754e]="桒", [0x754f]="・", [0x7550]="椑",
        [0x7551]="椻", [0x7552]="橅", [0x7553]="檑", [0x7554]="櫛",
        [0x7555]="・", [0x7556]="・", [0x7557]="・", [0x7558]="毱",
        [0x7559]="泠", [0x755a]="洮", [0x755b]="海", [0x755c]="涿",
        [0x755d]="淊", [0x755e]="淸", [0x755f]="渚", [0x7560]="潞",
        [0x7561]="濹", [0x7562]="灤", [0x7563]="・", [0x7564]="・",
        [0x7565]="煇", [0x7566]="燁", [0x7567]="爀", [0x7568]="玟",
        [0x7569]="・", [0x756a]="珉", [0x756b]="珖", [0x756c]="琛",
        [0x756d]="琡", [0x756e]="琢", [0x756f]="琦", [0x7570]="琪",
        [0x7571]="琬", [0x7572]="琹", [0x7573]="瑋", [0x7574]="㻚",
        [0x7575]="畵", [0x7576]="疁", [0x7577]="睲", [0x7578]="䂓",
        [0x7579]="磈", [0x757a]="磠", [0x757b]="祇", [0x757c]="禮",
        [0x757d]="・", [0x757e]="・", [0x7621]="・", [0x7622]="秚",
        [0x7623]="稞", [0x7624]="筿", [0x7625]="簱", [0x7626]="䉤",
        [0x7627]="綋", [0x7628]="羡", [0x7629]="脘", [0x762a]="脺",
        [0x762b]="・", [0x762c]="芮", [0x762d]="葛", [0x762e]="蓜",
        [0x762f]="蓬", [0x7630]="蕙", [0x7631]="藎", [0x7632]="蝕",
        [0x7633]="蟬", [0x7634]="蠋", [0x7635]="裵", [0x7636]="角",
        [0x7637]="諶", [0x7638]="跎", [0x7639]="辻", [0x763a]="迶",
        [0x763b]="郝", [0x763c]="鄧", [0x763d]="鄭", [0x763e]="醲",
        [0x763f]="鈳", [0x7640]="銈", [0x7641]="錡", [0x7642]="鍈",
        [0x7643]="閒", [0x7644]="雞", [0x7645]="餃", [0x7646]="饀",
        [0x7647]="髙", [0x7648]="鯖", [0x7649]="鷗", [0x764a]="麴",
        [0x764b]="麵"
    };

    private static string DecodeJis(byte first, byte second) => Japanese.GetString(
        [0x1b,0x24,0x42,first,second,0x1b,0x28,0x42]);

    private static string DecodeKanji(byte first, byte second)
    {
        int code = (first << 8) | second;
        return AribExtendedKanji.TryGetValue(code, out int unicode)
            ? char.ConvertFromUtf32(unicode)
            : DecodeJis(first, second);
    }

    private static string DecodeDrcs(AribDrcsStore? drcs, int set, int code)
    {
        // The bitmap is retained by AribDrcsStore for a bitmap-capable presenter.
        // Keep the established text fallback for the current text-only caption UI.
        return drcs is not null && drcs.TryGet(set, code, out _) ? "〓" : "〓";
    }

    // ARIB Kanji rows 92-94 are assigned to broadcast symbols and are not
    // JIS X 0208 characters.  ISO-2022-JP therefore decodes them as unrelated
    // ideographs (for example 0x7c21 becomes "釗") or as '?'.
    private static readonly IReadOnlyDictionary<int, int> AribExtendedKanji =
        new Dictionary<int, int>
        {
            [0x7c21] = 0x27a1, [0x7c22] = 0x2b05, [0x7c23] = 0x2b06, [0x7c24] = 0x2b07,
            [0x7c25] = 0x2b2f, [0x7c26] = 0x2b2e, [0x7c27] = 0x5e74, [0x7c28] = 0x6708,
            [0x7c29] = 0x65e5, [0x7c2a] = 0x5186, [0x7c2b] = 0x33a1, [0x7c2c] = 0x33a5,
            [0x7c2d] = 0x339d, [0x7c2e] = 0x33a0, [0x7c2f] = 0x33a4, [0x7c30] = 0x1f100,
            [0x7c31] = 0x2488, [0x7c32] = 0x2489, [0x7c33] = 0x248a, [0x7c34] = 0x248b,
            [0x7c35] = 0x248c, [0x7c36] = 0x248d, [0x7c37] = 0x248e, [0x7c38] = 0x248f,
            [0x7c39] = 0x2490, [0x7c40] = 0x1f101, [0x7c41] = 0x1f102, [0x7c42] = 0x1f103,
            [0x7c43] = 0x1f104, [0x7c44] = 0x1f105, [0x7c45] = 0x1f106, [0x7c46] = 0x1f107,
            [0x7c47] = 0x1f108, [0x7c48] = 0x1f109, [0x7c49] = 0x1f10a, [0x7c4a] = 0x3233,
            [0x7c4b] = 0x3236, [0x7c4c] = 0x3232, [0x7c4d] = 0x3231, [0x7c4e] = 0x3239,
            [0x7c4f] = 0x3244, [0x7c50] = 0x25b6, [0x7c51] = 0x25c0, [0x7c52] = 0x3016,
            [0x7c53] = 0x3017, [0x7c54] = 0x27d0, [0x7c55] = 0x00b2, [0x7c56] = 0x00b3,
            [0x7c57] = 0x1f12d, [0x7c76] = 0x1f12c, [0x7c77] = 0x1f12b, [0x7c78] = 0x3247,
            [0x7c79] = 0x1f14f, [0x7c7a] = 0x1f229, [0x7c7b] = 0x213b,
            [0x7d21] = 0x322a, [0x7d22] = 0x322b, [0x7d23] = 0x322c, [0x7d24] = 0x322d,
            [0x7d25] = 0x322e, [0x7d26] = 0x322f, [0x7d27] = 0x3230, [0x7d28] = 0x3237,
            [0x7d29] = 0x337e, [0x7d2a] = 0x337d, [0x7d2b] = 0x337c, [0x7d2c] = 0x337b,
            [0x7d2d] = 0x2116, [0x7d2e] = 0x2121, [0x7d2f] = 0x3036, [0x7d30] = 0x26be,
            [0x7d31] = 0x1f246, [0x7d32] = 0x1f240, [0x7d33] = 0x1f241, [0x7d34] = 0x1f243,
            [0x7d35] = 0x1f247, [0x7d36] = 0x1f244, [0x7d37] = 0x1f248, [0x7d38] = 0x1f242,
            [0x7d39] = 0x1f245, [0x7d3a] = 0x1f12a, [0x7d3b] = 0x1f223, [0x7d3c] = 0x1f225,
            [0x7d3d] = 0x1f210, [0x7d3e] = 0x1f213, [0x7d3f] = 0x1f211, [0x7d40] = 0x1f230,
            [0x7d41] = 0x1f21f, [0x7d42] = 0x1f212, [0x7d43] = 0x1f219, [0x7d44] = 0x1f224,
            [0x7d45] = 0x1f22f, [0x7d46] = 0x1f222, [0x7d47] = 0x2113, [0x7d48] = 0x338f,
            [0x7d49] = 0x3390, [0x7d4a] = 0x33ca, [0x7d4b] = 0x339e, [0x7d4c] = 0x33a2,
            [0x7d4d] = 0x3371, [0x7d50] = 0x00bd, [0x7d51] = 0x2189, [0x7d52] = 0x2153,
            [0x7d53] = 0x2154, [0x7d54] = 0x00bc, [0x7d55] = 0x00be, [0x7d56] = 0x2155,
            [0x7d57] = 0x2156, [0x7d58] = 0x2157, [0x7d59] = 0x2158, [0x7d5a] = 0x2159,
            [0x7d5b] = 0x215a, [0x7d5c] = 0x2150, [0x7d5d] = 0x215b, [0x7d5e] = 0x2151,
            [0x7d5f] = 0x2152, [0x7d60] = 0x2600, [0x7d61] = 0x2601, [0x7d62] = 0x2602,
            [0x7d63] = 0x26c4, [0x7d64] = 0x2616, [0x7d65] = 0x2617, [0x7d66] = 0x26c9,
            [0x7d67] = 0x26ca, [0x7d68] = 0x2666, [0x7d69] = 0x2665, [0x7d6a] = 0x2663,
            [0x7d6b] = 0x2660, [0x7d6c] = 0x26cb, [0x7d6d] = 0x2a00, [0x7d6e] = 0x203c,
            [0x7d6f] = 0x2049, [0x7d70] = 0x26c5, [0x7d71] = 0x2614, [0x7d72] = 0x26c6,
            [0x7d73] = 0x2603, [0x7d74] = 0x26c7, [0x7d75] = 0x26a1, [0x7d76] = 0x26c8,
            [0x7d78] = 0x269e, [0x7d79] = 0x269f, [0x7d7a] = 0x266c, [0x7d7b] = 0x260e,
            [0x7e21] = 0x2160, [0x7e22] = 0x2161, [0x7e23] = 0x2162, [0x7e24] = 0x2163,
            [0x7e25] = 0x2164, [0x7e26] = 0x2165, [0x7e27] = 0x2166, [0x7e28] = 0x2167,
            [0x7e29] = 0x2168, [0x7e2a] = 0x2169, [0x7e2b] = 0x216a, [0x7e2c] = 0x216b,
            [0x7e2d] = 0x2470, [0x7e2e] = 0x2471, [0x7e2f] = 0x2472, [0x7e30] = 0x2473,
            [0x7e31] = 0x2474, [0x7e32] = 0x2475, [0x7e33] = 0x2476, [0x7e34] = 0x2477,
            [0x7e35] = 0x2478, [0x7e36] = 0x2479, [0x7e37] = 0x247a, [0x7e38] = 0x247b,
            [0x7e39] = 0x247c, [0x7e3a] = 0x247d, [0x7e3b] = 0x247e, [0x7e3c] = 0x247f,
            [0x7e3d] = 0x3251, [0x7e3e] = 0x3252, [0x7e3f] = 0x3253, [0x7e40] = 0x3254,
            [0x7e41] = 0x1f110, [0x7e42] = 0x1f111, [0x7e43] = 0x1f112, [0x7e44] = 0x1f113,
            [0x7e45] = 0x1f114, [0x7e46] = 0x1f115, [0x7e47] = 0x1f116, [0x7e48] = 0x1f117,
            [0x7e49] = 0x1f118, [0x7e4a] = 0x1f119, [0x7e4b] = 0x1f11a, [0x7e4c] = 0x1f11b,
            [0x7e4d] = 0x1f11c, [0x7e4e] = 0x1f11d, [0x7e4f] = 0x1f11e, [0x7e50] = 0x1f11f,
            [0x7e51] = 0x1f120, [0x7e52] = 0x1f121, [0x7e53] = 0x1f122, [0x7e54] = 0x1f123,
            [0x7e55] = 0x1f124, [0x7e56] = 0x1f125, [0x7e57] = 0x1f126, [0x7e58] = 0x1f127,
            [0x7e59] = 0x1f128, [0x7e5a] = 0x1f129, [0x7e5b] = 0x3255, [0x7e5c] = 0x3256,
            [0x7e5d] = 0x3257, [0x7e5e] = 0x3258, [0x7e5f] = 0x3259, [0x7e60] = 0x325a,
            [0x7e61] = 0x2460, [0x7e62] = 0x2461, [0x7e63] = 0x2462, [0x7e64] = 0x2463,
            [0x7e65] = 0x2464, [0x7e66] = 0x2465, [0x7e67] = 0x2466, [0x7e68] = 0x2467,
            [0x7e69] = 0x2468, [0x7e6a] = 0x2469, [0x7e6b] = 0x246a, [0x7e6c] = 0x246b,
            [0x7e6d] = 0x246c, [0x7e6e] = 0x246d, [0x7e6f] = 0x246e, [0x7e70] = 0x246f,
            [0x7e71] = 0x2776, [0x7e72] = 0x2777, [0x7e73] = 0x2778, [0x7e74] = 0x2779,
            [0x7e75] = 0x277a, [0x7e76] = 0x277b, [0x7e77] = 0x277c, [0x7e78] = 0x277d,
            [0x7e79] = 0x277e, [0x7e7a] = 0x277f, [0x7e7b] = 0x24eb, [0x7e7c] = 0x24ec,
            [0x7e7d] = 0x325b
        };

    private static string DecodeKana(Set set, byte code)
    {
        if (code is >= 0x77 and <= 0x7e)
        {
            string tail = set == Set.Hiragana ? "ゝゞー。「」、・" : "ヽヾー。「」、・";
            return tail[code - 0x77].ToString();
        }
        return DecodeJis(set == Set.Hiragana ? (byte)0x24 : (byte)0x25, code);
    }
}
