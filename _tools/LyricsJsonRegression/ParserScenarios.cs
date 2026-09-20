using Lyricify.Lyrics.Generators;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using System.Text;

// Compiled against BOTH the original library and the migrated library.
internal static class ParserScenarios
{
    internal static IEnumerable<string> Run()
    {
        foreach (string sync in new[] { "UNSYNCED", "LINE_SYNCED", "SYLLABLE_SYNCED", "unknown" })
        {
            string json = "{\"lyrics\":{\"syncType\":\"" + sync + "\",\"provider\":\"test\",\"providerLyricsId\":123,\"language\":\"zh\",\"lines\":[{\"startTimeMs\":1000,\"endTimeMs\":\"2200\",\"words\":\"你好\",\"syllables\":[{\"startTimeMs\":1000,\"endTimeMs\":\"1600\",\"numChars\":1},{\"startTimeMs\":\"1600\",\"endTimeMs\":2200,\"numChars\":\"1\"}]},{\"startTimeMs\":\"bad\",\"words\":\"终行\"}]}}";
            yield return Snapshot(SpotifyParser.Parse(json));
        }
        foreach (string json in new[] { "null", "{}", "{\"lyrics\":null}", "", " \r\n " })
            yield return Snapshot(SpotifyParser.Parse(json));

        const string rich = "[{\"ts\":1.123,\"te\":3.456,\"l\":[{\"c\":\"你\",\"o\":0},{\"c\":\"好\",\"o\":0.789}],\"x\":\"你好\"}]";
        string richBody = Quote(rich);
        const string subtitle = "\"[00:01.23]字幕\\n[00:02.50]下一行\"";
        foreach (string status in new[] { "200", "404", "\"200\"", "200.0", "null" })
        {
            string json = "{\"message\":{\"body\":{\"macro_calls\":{" +
                "\"track.richsync.get\":{\"message\":{\"header\":{\"status_code\":" + status + "},\"body\":{\"richsync\":{\"richsync_body\":" + richBody + ",\"richssync_language\":\"zh\",\"richsync_language\":\"en\"}}}}," +
                "\"track.subtitles.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"subtitle_list\":[{\"subtitle\":{\"subtitle_body\":" + subtitle + ",\"subtitle_language\":\"zh\"}}]}}}}}}}";
            yield return Capture(() => MusixmatchParser.Parse(json));
            yield return Snapshot(MusixmatchParser.Parse(json, true));
        }
        foreach (string json in new[] { "{}", "{\"message\":null}", "{\"message\":{\"body\":{\"macro_calls\":{}}}}" })
            yield return Capture(() => MusixmatchParser.Parse(json));
        yield return Snapshot(MusixmatchParser.Parse("{\"message\":{\"body\":{\"macro_calls\":{\"track.lyrics.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"lyrics\":{\"lyrics_body\":\"  hello\\n\\nworld  \"}}}}}}}}"));
        yield return Snapshot(YrcParser.Parse("{\"t\":0,\"c\":[{\"tx\":\"作词：\"},{\"tx\":\"作者\"}]}\n[1000,1000](1000,500,0)你(1500,500,0)好\n{\"t\":5000,\"c\":[{\"tx\":\"结束\"}]}\n"));
        string translation = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"content\":[{\"language\":0,\"type\":1,\"lyricContent\":[[\"译文\"],[\"第二行\"]]}],\"version\":1}"));
        string krc = "[language:" + translation + "]\n[1000,1000]<0,500,0>你<500,500,0>好";
        yield return Snapshot(KrcParser.Parse(krc));
        yield return string.Join('|', KrcTranslationParser.GetTranslationFromKrc(krc)!);
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Capture(Func<LyricsData?> parse)
    {
        try { return Snapshot(parse()); }
        catch (InvalidOperationException) { return "InvalidOperationException"; }
    }

    private static string Snapshot(LyricsData? lyrics)
    {
        if (lyrics is null) return "null";
        var result = new StringBuilder();
        result.Append(lyrics.File?.Type).Append('|').Append(lyrics.File?.SyncTypes).Append('|');
        if (lyrics.Writers is { } writers) result.AppendJoin(',', writers);
        result.Append('|');
        if (lyrics.TrackMetadata is { } track)
        {
            result.Append(track.Title).Append('|').Append(track.Artist).Append('|').Append(track.Album).Append('|')
                .Append(track.AlbumArtist).Append('|').Append(track.DurationMs).Append('|').Append(track.Isrc).Append('|');
            if (track.Language is { } languages) result.AppendJoin(',', languages);
        }
        if (lyrics.File?.AdditionalInfo is SpotifyAdditionalInfo spotify)
            result.Append('|').Append(spotify.Provider).Append('|').Append(spotify.ProviderLyricsId).Append('|')
                .Append(spotify.ProviderDisplayName).Append('|').Append(spotify.LyricsLanguage);
        if (lyrics.Lines is { } lines)
            foreach (var line in lines) AppendLine(line);
        result.Append("|KRC:").Append(KrcGenerator.Generate(lyrics));
        result.Append("|YRC:").Append(YrcGenerator.Generate(lyrics));
        result.Append("|LRC:").Append(LrcGenerator.Generate(lyrics));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(result.ToString()));

        void AppendLine(ILineInfo line)
        {
            result.Append('|').Append(line.Text).Append(':').Append(line.StartTime).Append(':').Append(line.EndTime)
                .Append(':').Append(line.LyricsAlignment);
            if (line is SyllableLineInfo syllableLine)
                foreach (var syllable in syllableLine.Syllables)
                    result.Append('|').Append(syllable.Text).Append(':').Append(syllable.StartTime).Append(':').Append(syllable.EndTime);
            if (line.SubLine is { } subline) AppendLine(subline);
        }
    }
}
