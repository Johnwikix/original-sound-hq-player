using Lyricify.Lyrics.Parsers;

partial class Program
{
    static void CheckParsers()
    {
        var lyrics = SpotifyParser.Parse("{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[{\"startTimeMs\":1000,\"endTimeMs\":\"2000\",\"words\":\"歌词\"}]}}");
        Equal("歌词", lyrics!.Lines![0].Text, "Spotify text");
        Equal("1000", lyrics.Lines[0].StartTime.ToString(), "Spotify numeric timestamp");
        Equal("2000", lyrics.Lines[0].EndTime.ToString(), "Spotify string timestamp");
        Equal(null, SpotifyParser.Parse("null")?.Lines?.Count.ToString(), "Spotify null");
        Equal(null, MusixmatchParser.Parse("{}")?.Lines?.Count.ToString(), "Musixmatch empty");
        var plain = MusixmatchParser.Parse("{\"message\":{\"body\":{\"macro_calls\":{\"track.lyrics.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"lyrics\":{\"lyrics_body\":\"first\\nsecond\"}}}}}}}}");
        Equal("first", plain!.Lines![0].Text, "Musixmatch plain first line");
        Equal("second", plain.Lines[1].Text, "Musixmatch plain second line");
        var credits = YrcParser.Parse("{\"t\":0,\"c\":[{\"tx\":\"作词：\"},{\"tx\":\"作者\"}]}\n[1000,1000](1000,500,0)你(1500,500,0)好\n");
        Equal("作者", credits.Writers![0], "YRC writer");
        Equal("你好", credits.Lines![1].Text, "YRC lyrics");
        string translation = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"content\":[{\"language\":0,\"type\":1,\"lyricContent\":[[\"译文\"]]}],\"version\":1}"));
        string krc = "[language:" + translation + "]\n[1000,1000]<0,1000,0>hello";
        Equal("True", KrcTranslationParser.CheckKrcTranslation(krc).ToString(), "KRC translation exists");
        Equal("译文", KrcTranslationParser.GetTranslationFromKrc(krc)![0], "KRC translation");
        Equal("False", KrcTranslationParser.CheckKrcTranslation("[language:invalid]").ToString(), "KRC invalid base64");
    }
}
