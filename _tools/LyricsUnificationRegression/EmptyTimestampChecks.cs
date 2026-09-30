using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

/// <summary>Exercises empty rows and word groups through parsing and the actual sidecar resolver.</summary>
internal static class EmptyTimestampChecks
{
    private const string Sample = """
        [00:17.515]Don't turn around
        [00:18.515]
        [00:18.515]
        [00:18.515]
        [00:18.515]
        [00:20.859]I'm only a heartbeat away
        [00:25.195]I found clarity in the mess I'd always make
        [00:32.431]The ocean dressed in silver light
        """;
    private static readonly double[] SampleStarts = [17515, 20859, 25195, 32431];
    private const string TtmlSample = """
        <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
        <p begin="1s" end="2s"><span begin="1s" end="1.4s">A</span><span begin="1.4s" end="1.4s"/><span begin="1.4s" end="1.4s"/><span begin="1.4s" end="2s">B</span></p>
        <p begin="2s" end="2.5s"><span begin="2s" end="2.5s"> </span></p>
        <p begin="3s" end="4s"><span begin="3s" end="4s">C</span></p>
        </div></body></tt>
        """;
    private const string TtmlEmpty = """
        <tt xmlns="http://www.w3.org/ns/ttml"><body><p begin="2s" end="2.5s"><span begin="2s" end="2.5s"> </span></p></body></tt>
        """;

    internal static async Task RunAsync(LyricsParser parser, LyricsRefreshService resolver,
        string folder, Action<bool, string> check)
    {
        int failures = 0;
        void Expect(bool condition, string message)
        {
            if (condition) check(true, message);
            else { failures++; Console.WriteLine("FAIL empty timestamps: " + message); }
        }
        async Task CheckSample(string name, string content)
        {
            var document = parser.Import(content);
            var parsed = parser.Parse(document, 40000);
            Expect(document.Original.Format == LyricsFormat.Lrc && parsed.Length == 4 &&
                parsed.Select((line, i) => Math.Abs(line.StartMs - SampleStarts[i]) < 0.001).All(value => value),
                name + ": repeated blank timestamps do not create rows or change valid entrances");
            string path = Path.Combine(folder, "empty-timestamps-" + name + ".flac");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), content);
            var displayed = await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromSeconds(40) }, default);
            Expect(displayed.Count == 4 && displayed[0].EndMs == 20859 && displayed[0].HighlightEndMs == 20859 &&
                displayed.All(line => line.Words.Count > 0),
                name + ": highlight holds through 18.515s until the real 20.859s entrance");
            Expect(Math.Abs(displayed[0].Words[^1].StartMs + displayed[0].Words[^1].DurationMs - 20559) < 0.001 &&
                File.ReadAllText(Path.ChangeExtension(path, ".lrc")) == content,
                name + ": synthetic words keep the 300ms margin without rewriting empty source rows");
        }
        foreach (var (name, separator) in new[] { ("CR", "\r"), ("LF", "\n"), ("CRLF", "\r\n") })
            await CheckSample(name, Sample.ReplaceLineEndings(separator));
        await CheckSample("whitespace", Sample.Replace("[00:18.515]", "[00:18.515] \t").ReplaceLineEndings("\r"));

        foreach (var (format, content, emptyOnly) in new[]
        {
            ("QRC", "[1000,1000]A(1000,400)(1400,0)(1400,0)B(1400,600)\n[2000,500](2000,0)(2000,0)\n[3000,1000]C(3000,1000)", "[2000,500](2000,0)(2000,0)"),
            ("KRC", "[1000,1000]<0,400,0>A<400,0,0><400,0,0><400,600,0>B\n[2000,500]<0,0,0><0,0,0>\n[3000,1000]<0,1000,0>C", "[2000,500]<0,0,0><0,0,0>"),
            ("QRC-whitespace", "[1000,1000]A(1000,400)B(1400,600)\n[2000,500] \t(2000,500) (2500,0)\n[3000,1000]C(3000,1000)", "[2000,500] \t(2000,500) (2500,0)"),
            ("KRC-whitespace", "[1000,1000]<0,400,0>A<400,600,0>B\n[2000,500]<0,500,0> \t<500,0,0> \n[3000,1000]<0,1000,0>C", "[2000,500]<0,500,0> \t<500,0,0> "),
            ("enhanced", "[00:01.000]<00:01.000>A<00:01.400><00:01.400>B<00:02.000>\n[00:02.000]<00:02.000><00:02.000>\n[00:03.000]<00:03.000>C<00:04.000>", "[00:02.000]<00:02.000><00:02.000>"),
            ("enhanced-whitespace", "[00:01.000]<00:01.000>A<00:01.400>B<00:02.000>\n[00:02.000]<00:02.000> \t<00:02.500>\n[00:03.000]<00:03.000>C<00:04.000>", "[00:02.000]<00:02.000> \t<00:02.500>"),
            ("TTML", TtmlSample, TtmlEmpty)
        })
        {
            var document = parser.Import(content);
            var parsed = parser.Parse(document, 5000);
            Expect(parsed.Length == 2 && parsed[0].Text == "AB" && parsed[0].Words.Length == 2 &&
                parsed[0].Words[1].StartMs == 1400 && parsed[1].StartMs == 3000,
                format + ": empty word groups leave subsequent valid word timestamps unchanged and empty rows absent");
            Expect(!parser.HasLyrics(parser.Import(emptyOnly)), format + ": empty-only timed content is not valid lyrics");
            string path = Path.Combine(folder, "empty-words-" + format + ".flac");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), content);
            var displayed = await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromSeconds(5) }, default);
            Expect(displayed.Count == 2 && displayed[0].HighlightEndMs == 3000 &&
                displayed[0].Words.All(word => word.Word.Length > 0) &&
                displayed[0].Words[^1].StartMs == (format == "TTML" ? 1400 : 1280) &&
                displayed[0].Words[^1].DurationMs == (format == "TTML" ? 600 : 420),
                format + ": empty words do not interrupt highlight or change the format's playback timing");
        }

        const string spaces = "[00:01.000]<00:01.000>A<00:01.400> <00:01.600>B<00:02.000>";
        var spaced = parser.Parse(parser.Import(spaces), 5000);
        Expect(spaced.Length == 1 && spaced[0].Text == "A B" && spaced[0].Words.Length == 3 &&
            spaced[0].Words[1].Text == " " && spaced[0].Words[1].StartMs == 1400 && spaced[0].Words[2].StartMs == 1600,
            "timed spaces inside real lyrics retain both word separation and the pause's timestamps");
        var instant = parser.Parse(parser.Import("[1000,1000]A(1000,0)B(1000,1000)"), 5000);
        Expect(instant.Length == 1 && instant[0].Words.Length == 2 && instant[0].Words[0].Text == "A" &&
            instant[0].Words[0].StartMs == instant[0].Words[0].EndMs,
            "a zero-duration word with real text remains an instant lyric rather than an empty group");
        foreach (var (format, content) in new[]
        {
            ("QRC", "[1000,1000]A(1000,400)(1400,200)(1600,0)B(1600,400)"),
            ("KRC", "[1000,1000]<0,400,0>A<400,200,0><600,0,0><600,400,0>B"),
            ("enhanced", "[00:01.000]A<00:01.400><00:01.600><00:01.600>B<00:02.000>")
        })
        {
            var gap = parser.Parse(parser.Import(content), 5000);
            Expect(gap.Length == 1 && gap[0].Words.Length == 2 && gap[0].Words[0].EndMs == 1400 &&
                gap[0].Words[1].StartMs == 1600 && gap[0].Words[1].EndMs == 2000,
                format + ": timed empty group retains the 200ms pause between valid words");
            string path = Path.Combine(folder, "empty-word-pause-" + format + ".flac");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), content);
            var displayed = await resolver.SetLyrics(new() { Path = path }, default);
            Expect(displayed[0].Words.Count == 2 && displayed[0].Words[1].StartMs == 1420 &&
                displayed[0].Words[1].DurationMs == 280,
                format + ": dropping empty words retains the pause after legacy 300ms projection");
        }
        string fallbackPath = Path.Combine(folder, "empty-timed-fallback.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(fallbackPath, ".krc"), "[1000,1000]<0,0,0><0,0,0>");
        await File.WriteAllTextAsync(Path.ChangeExtension(fallbackPath, ".lrc"), "[00:01.000]Valid fallback");
        var fallback = await resolver.SetLyrics(new() { Path = fallbackPath }, default);
        Expect(fallback.Count == 1 && string.Concat(fallback[0].Words.Select(word => word.Word)) == "Valid fallback",
            "an empty-only preferred timed file does not mask a valid lower-priority sidecar");
        check(failures == 0, $"empty timestamp and word-group regression ({failures} failures)");
    }
}
