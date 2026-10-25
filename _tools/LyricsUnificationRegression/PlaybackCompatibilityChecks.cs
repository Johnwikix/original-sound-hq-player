using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

/// <summary>Golden playback values from 44597b0a's ParseEnhancedWords / FixEndMs, not the new parser.</summary>
internal static class PlaybackCompatibilityChecks
{
    internal static async Task RunAsync(LyricsParser parser, LyricsRefreshService resolver,
        string folder, Action<bool, string> check)
    {
        int failures = 0;
        async Task<List<LyricLine>> Display(string name, string content, double duration = 10000)
        {
            string path = Path.Combine(folder, "compatibility-" + name + ".flac");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), content);
            return await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromMilliseconds(duration) }, default);
        }
        void Expect(bool condition, string description)
        {
            if (!condition) { failures++; Console.WriteLine("FAIL compatibility: " + description); }
            else check(true, "compatibility: " + description);
        }
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.000001;

        // The line header ends at 2s while its last word starts at 2.1s. Previously
        // projection shortened 1.2s of word timing to 0.9s, finishing at 1.9s.
        const string lateWord = "[1000,1000]A(1000,1100)B(2100,100)\n[2000,1000]C(2000,1000)";
        var lines = await Display("late-word", lateWord);
        Expect(Near(lines[0].Words[^1].StartMs, 1825) && Near(lines[0].Words[^1].DurationMs, 75),
            "last word starts at 1825ms and finishes at 1900ms before the 2000ms row switch");

        const string gap = "[1000,1000]A(1000,400)B(1400,600)\n[5000,1000]C(5000,1000)";
        lines = await Display("gap", gap);
        Expect(lines[0].EndMs == 5000 && Near(lines[0].Words[1].StartMs, 1280) && Near(lines[0].Words[1].DurationMs, 420),
            "legacy line stays active until the next entrance, with words finishing 300ms earlier");

        const string krc = "[1000,1000]<0,400,0>A<400,600,0>B\n[5000,1000]<0,1000,0>C";
        lines = await Display("krc", krc);
        Expect(lines[0].EndMs == 5000 && Near(lines[0].Words[1].StartMs, 1280) && Near(lines[0].Words[1].DurationMs, 420),
            "standard KRC keeps the same playback timing as equivalent legacy QRC");

        foreach (string content in new[]
        {
            "[00:01.000]<00:01.000>A<00:01.400>B<00:01.800>C\n[00:03.000]D",
            "[00:01.000]A[00:01.400]B[00:01.800]C\n[00:03.000]D"
        })
        {
            lines = await Display("enhanced-" + failures, content);
            Expect(Near(lines[0].Words[^1].StartMs, 1500) && lines[0].Words[^1].DurationMs == 0,
                "enhanced LRC without a closing timestamp preserves the instant final word at 1500ms");
            Expect(lines[^1].EndMs == 12000 && Near(lines[^1].Words[0].DurationMs, 8700),
                "untimed row inside an enhanced LRC retains legacy simulated word timing");
        }

        lines = await Display("syllables", "[1000,1200]你好(1000,1200)\n[3000,1000]Next(3000,1000)");
        Expect(lines[0].Words.Count == 2 && lines[0].Words[0].Word == "你" && lines[0].Words[1].Word == "好" &&
            Near(lines[0].Words[0].DurationMs, 450) && Near(lines[0].Words[1].StartMs, 1450),
            "multi-character segments retain per-token syllables and their old effect thresholds");
        lines = await Display("short", "[1000,200]A(1000,200)\n[1200,1000]B(1200,1000)");
        Expect(lines[0].Words[0].StartMs == 1000 && lines[0].Words[0].DurationMs == 0,
            "a line shorter than 300ms completes its word instantly, without negative duration");
        lines = await Display("last", "[00:01.000]A B", 0);
        Expect(lines[0].EndMs == 10500 && Near(lines[0].Words[^1].StartMs + lines[0].Words[^1].DurationMs, 10200),
            "unknown song duration retains the legacy 10500ms fallback and 300ms finish margin");

        var document = parser.Import(lateWord);
        var parsed = parser.Parse(document, 10000);
        Expect(parsed[0].EndMs == 2000 && parsed[0].Words[^1].StartMs == 2100 &&
            parsed[0].Words[^1].EndMs == 2200 && document.Original.Content == lateWord,
            "playback compatibility does not rewrite source parsing, stored text or explicit word timestamps");
        string fixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string song = await File.ReadAllTextAsync(Path.Combine(fixtureFolder, "story-of-us.qrc.txt"));
        var golden = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(fixtureFolder, "story-of-us.playback.json")),
            PlaybackGoldenJsonContext.Default.ListLyricLine)!;
        lines = await Display("story-of-us", song, 265000);
        Expect(lines.Count == golden.Count, "The Story of Us: complete song keeps all legacy rows");
        for (int i = 0; i < golden.Count; i++)
        {
            var expectedWords = golden[i].Words.Where(word => !string.IsNullOrWhiteSpace(word.Word)).ToArray();
            var actualWords = lines[i].Words.Where(word => !string.IsNullOrWhiteSpace(word.Word)).ToArray();
            // The old scanner mistakes literal '(' for a timing delimiter in these two
            // rows. Keep the corrected source text rather than restoring that data loss.
            bool hasLiteralParenthesis = i is 0 or 47;
            Expect(lines[i].StartMs == golden[i].StartMs && lines[i].EndMs == golden[i].EndMs &&
                (hasLiteralParenthesis
                    ? expectedWords.All(word => actualWords.Any(actual => word.Word == actual.Word &&
                        Near(word.StartMs, actual.StartMs) && Near(word.DurationMs, actual.DurationMs)))
                    : expectedWords.Length == actualWords.Length && expectedWords.Select((word, j) =>
                        word.Word == actualWords[j].Word && Near(word.StartMs, actualWords[j].StartMs) &&
                        Near(word.DurationMs, actualWords[j].DurationMs)).All(value => value)),
                $"The Story of Us: row {i} text and syllable timing match executed pre-refactor code");
        }
        Expect(string.Concat(lines[0].Words.Select(word => word.Word)) == "The Story of Us - milet (ミレイ)" &&
            string.Concat(lines[47].Words.Select(word => word.Word)) == "縫い目をゆく (We are )",
            "literal parentheses retain source text that the old scanner incorrectly dropped");
        var brave = lines.Single(line => line.StartMs == 153255).Words[^1];
        Expect(brave.Word == "brave" && Near(brave.StartMs, 156266.0664256665) &&
            Near(brave.StartMs + brave.DurationMs, 157381),
            "reported brave finishes at 157381ms, leaving 300ms before the 157681ms entrance");
        check(failures == 0, $"legacy playback golden values ({failures} mismatches)");
    }
}

[JsonSerializable(typeof(List<LyricLine>))]
internal partial class PlaybackGoldenJsonContext : JsonSerializerContext;
