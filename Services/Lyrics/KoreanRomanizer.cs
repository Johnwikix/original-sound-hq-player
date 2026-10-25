using System.Text;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>Revised Romanization tables ported from @romanize/korean.</summary>
internal static class KoreanRomanizer
{
    private static readonly string[] Initial =
        ["g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"];
    private static readonly string[] Medial =
        ["a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i"];
    private static readonly string[] Final =
        ["", "k", "k", "k", "n", "n", "n", "t", "l", "l", "m", "l", "l", "l", "l", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t"];
    private static readonly string?[,] NextFinal = CreateNextFinalTable();

    public static string Romanize(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (value is < '\uAC00' or > '\uD7A3')
            {
                builder.Append(value);
                continue;
            }

            int code = value - '\uAC00';
            int initial = code / 588;
            int medial = code % 588 / 28;
            int final = code % 28;
            string? convertedFinal = Final[final];
            if (final != 0 && index + 1 < text.Length && IsSyllable(text[index + 1]))
            {
                int nextCode = text[index + 1] - '\uAC00';
                int nextInitial = nextCode / 588;
                string? next = NextFinal[final, nextInitial];
                if (next is not null) convertedFinal = next;
            }
            builder.Append(Initial[initial]).Append(Medial[medial]).Append(convertedFinal);
        }
        return builder.ToString();
    }

    private static bool IsSyllable(char value) => value is >= '\uAC00' and <= '\uD7A3';

    private static string?[,] CreateNextFinalTable()
    {
        var result = new string[28, 19];
        void Add(int final, string value, params int[] initials)
        {
            foreach (int initial in initials) result[final, initial] = value;
        }
        Add(1, "g", 11); Add(1, "ngn", 2, 5); Add(1, "ngm", 6); Add(1, "k", 18);
        Add(2, "kk", 11);
        Add(3, "ks", 11); Add(3, "ngn", 2, 5); Add(3, "ngm", 6); Add(3, "k", 18);
        Add(4, "ll", 5);
        Add(5, "nj", 11); Add(5, "nn", 2, 5); Add(5, "nm", 6); Add(5, "ch", 18);
        Add(6, "nh", 11); Add(6, "nk", 0); Add(6, "nn", 2, 5); Add(6, "nm", 6); Add(6, "nb", 7); Add(6, "ch", 18);
        Add(7, "j", 11); Add(7, "nn", 2, 5); Add(7, "nm", 6); Add(7, "ch", 18);
        Add(8, "r", 11);
        Add(9, "lg", 11); Add(10, "lm", 11); Add(11, "lb", 11); Add(12, "ls", 11);
        Add(13, "lt", 11); Add(14, "lp", 11); Add(15, "lh", 11);
        Add(17, "b", 11); Add(17, "mn", 2, 5); Add(17, "mm", 6); Add(17, "p", 18);
        Add(18, "ps", 11);
        Add(19, "s", 11); Add(19, "nn", 2, 5); Add(19, "nm", 6);
        Add(20, "ss", 11);
        Add(22, "j", 11); Add(22, "nn", 2, 5); Add(22, "nm", 6); Add(22, "ch", 18);
        Add(23, "ch", 11); Add(23, "nn", 2, 5); Add(23, "nm", 6); Add(23, "ch", 18);
        Add(25, "nn", 2, 5); Add(25, "nm", 6); Add(25, "ch", 18);
        Add(27, "h", 11); Add(27, "k", 0); Add(27, "kk", 1); Add(27, "nn", 2, 5);
        Add(27, "t", 3, 16); Add(27, "tt", 4); Add(27, "nm", 6); Add(27, "p", 7);
        Add(27, "pp", 8); Add(27, "s", 9); Add(27, "ss", 10); Add(27, "ch", 12); Add(27, "jj", 13); Add(27, "h", 18);
        return result;
    }
}
