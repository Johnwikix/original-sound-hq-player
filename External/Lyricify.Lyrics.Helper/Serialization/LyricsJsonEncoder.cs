using System.Text.Encodings.Web;

namespace Lyricify.Lyrics.Serialization;

// Match Json.NET's default escaping, including lowercase hex and unescaped non-ASCII/HTML.
// This encoder is for provider JSON payloads, never for embedding JSON in HTML.
internal sealed class LyricsJsonEncoder : JavaScriptEncoder
{
    internal static readonly LyricsJsonEncoder Instance = new();
    public override int MaxOutputCharactersPerInputCharacter => 6;
    public override bool WillEncode(int unicodeScalar) => unicodeScalar < 0x20 || unicodeScalar is '"' or '\\' or 0x85 or 0x2028 or 0x2029;

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (int i = 0; i < textLength; i++)
            if (WillEncode(text[i])) return i;
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        char shortEscape = unicodeScalar switch
        {
            '"' => '"', '\\' => '\\', '\b' => 'b', '\t' => 't', '\n' => 'n', '\f' => 'f', '\r' => 'r', _ => '\0',
        };
        int length = shortEscape == '\0' ? 6 : 2;
        numberOfCharactersWritten = 0;
        if (bufferLength < length) return false;
        buffer[0] = '\\';
        if (length == 2) buffer[1] = shortEscape;
        else
        {
            const string hex = "0123456789abcdef";
            buffer[1] = 'u';
            buffer[2] = hex[(unicodeScalar >> 12) & 15];
            buffer[3] = hex[(unicodeScalar >> 8) & 15];
            buffer[4] = hex[(unicodeScalar >> 4) & 15];
            buffer[5] = hex[unicodeScalar & 15];
        }
        numberOfCharactersWritten = length;
        return true;
    }
}
