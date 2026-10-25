using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

/// <summary>Checks real line boundaries in parsing, persistence and playback projection.</summary>
internal static class LineEndingChecks
{
    private const int SampleLineCount = 65;
    private const double DurationMs = 250000;

    // The complete reported LRC; test variants change only its physical line endings.
    private const string Sample = """
        [00:00.00] 作词 : F.L.O.A.T (白羽）/满舒克
        [00:01.00] 作曲 : Life Awaits/满舒克
        [00:02.00] 编曲 : Life Awaits
        [00:03.00] 录音 : 吕琳瑄
        [00:04.00] 混音 : 吕琳瑄
        [00:05.00] 母带 : 吕琳瑄
        [00:06.00] 录音工作室 : SuperWave Studio
        [00:07.00] 出品 : 星团音乐
        [00:08.00] 发行 : 智慧小狗音乐
        [00:25.19]Like a shooting star
        [00:28.19]Slowly roaming through the nebula
        [00:32.10]I am sending silence from afar
        [00:37.02]Will I fall apart
        [00:40.16]In the stellar wind that’s breathing in
        [00:43.96]Before I made an impact to its heart
        [00:49.83]I don’t know where I’m heading now
        [00:52.57]I need to find the truth somehow
        [00:55.58]I’d give my everything to touch your ground
        [01:01.93]Shine on me
        [01:06.49]The beauty in your halo
        [01:09.49]Lift me out of the shadow
        [01:13.85]Shine on me
        [01:18.32]Take me by your gravity
        [01:21.30]Cause I don’t wanna fall asleep
        [01:30.54]I’m giving my everything
        [01:33.35]Giving my everything
        [01:36.83]Cause I know what it takes
        [01:40.07]To amend my mistakes
        [01:43.52]I will fall for your grace
        [01:45.77]I’m disarmed
        [01:49.65]I don’t know where I’m heading now
        [01:52.65]I need to find the truth somehow
        [01:55.58]I’d give my everything to touch your ground
        [02:01.91]Shine on me
        [02:06.33]The beauty in your halo
        [02:09.43]Lift me out of the shadow
        [02:13.91]Shine on me
        [02:18.32]Take me by your gravity
        [02:21.34]I don’t wanna fall asleep
        [02:40.73](<I don’t wanna fall asleep>)
        [02:48.87](<Don’t let me>)
        [02:50.76]在没有尽头的宇宙
        [02:53.08]你会选择在哪停留
        [02:56.73]你我这一生的云游
        [02:58.98]算不算是种追求
        [03:01.78]正当我 穿过 沙漠 河流
        [03:04.56]你是否 也在 世界 另一头
        [03:07.59]或奋斗 或温柔 或不值一提的承受
        [03:13.72]漂浮着 迷茫着 沉淀着 的梦
        [03:16.39]在暮色 中附和 着苦涩 的痛
        [03:19.39]又如何 谁又能感知你的不同
        [03:24.90]我们看到不同的风景却能感受共鸣
        [03:28.63]I feel da vibe 像是悦耳动听
        [03:31.45]享受着爱哪怕周遭太轰鸣
        [03:34.24]我想你很聪明 你我都能共情
        [03:37.13]别轻易后退，别后退
        [03:43.61]I don’t wanna wait a lucky day
        [03:45.22]A lucky day
        [03:46.33](<Shine on me, Shine on me>)
        [03:46.43]用双手创造一个专属自己的机会
        [03:49.13]怎会心甘情愿Wait, Wait
        [03:51.60](<I need to find the truth, I’d give my everything to you>)
        [03:55.28]浩瀚的星辰也会有一个我闪烁的位置
        [03:58.37](<Take me by your gravity, I don’t wanna fall asleep>)
        [03:58.95]尽管让时光匆匆穿梭其中跳跃飞逝
        """;

    public static void Run(LyricsParser parser, Action<bool, string> check)
    {
        var mixed = new StringBuilder();
        int row = 0;
        foreach (var line in Sample.AsSpan().EnumerateLines())
            mixed.Append(line).Append((row++ % 3) switch { 0 => "\r", 1 => "\n", _ => "\r\n" });

        foreach (var (name, separator, content) in new[]
        {
            ("CR", "\r", Sample.ReplaceLineEndings("\r")),
            ("LF", "\n", Sample.ReplaceLineEndings("\n")),
            ("CRLF", "\r\n", Sample.ReplaceLineEndings("\r\n")),
            ("mixed", "\r", mixed.ToString())
        })
        {
            var document = parser.Import(content);
            var lines = parser.Parse(document, DurationMs);
            check(lines.Length == SampleLineCount, $"{name}: full LRC sample has 65 rows (actual {lines.Length})");
            check(document.Original.Format == LyricsFormat.Lrc && document.Original.Content == content,
                name + ": detection remains line timed and original text is preserved");
            check(MatchesSample(lines), name + ": every lyric text, timestamp and line end is preserved");
            check(MatchesSample(parser.Parse(parser.Import(parser.ExportLrc(document)), DurationMs)),
                name + ": normalized LRC export round trip retains all rows");

            string translation = "[00:25.19]译文一" + separator + "[03:58.95]译文二";
            // Stored V2 documents can contain the wrong format recorded by the old detector.
            var stored = new LyricsDocument(new(content, LyricsFormat.EnhancedLrc), translation);
            var recovered = parser.Parse(stored, DurationMs);
            check(MatchesSample(recovered) && recovered[9].Translation == "译文一" && recovered[^1].Translation == "译文二",
                name + ": existing document and raw translation are reparsed correctly");
            check(parser.NormalizeTranslation(translation) == "[00:25.190]译文一\n[03:58.950]译文二\n",
                name + ": translation normalization retains both rows");
        }

        var offset = parser.Parse(parser.Import("[ti:sample]\r[offset:-250]\r[00:01.00]A\r[00:02.00]B"), 4000);
        check(offset.Length == 2 && offset[0].StartMs == 750 && offset[1].StartMs == 1750,
            "CR metadata offset applies to every row");
        var repeated = parser.Parse(parser.Import("[00:01.00][00:03.00]A\r\r[00:04.00]B\r"), 5000);
        check(repeated.Length == 3 && repeated[1].StartMs == 3000 && repeated.All(line => line.Words.IsEmpty),
            "CR repeated timestamps and empty rows remain ordinary LRC");
        var enhanced = parser.Import("[00:01.00]<00:01.00>A<00:01.50>B<00:02.00>\r[00:03.00]<00:03.00>C<00:04.00>");
        var words = parser.Parse(enhanced, 5000);
        check(enhanced.Original.Format == LyricsFormat.EnhancedLrc && words.Length == 2 &&
            words[0].Words.Length == 2 && words[0].Words[1].StartMs == 1500 && words[1].Words[0].EndMs == 4000,
            "CR genuine enhanced LRC retains separate rows and word timing");
        check(parser.Parse(parser.Import("[00:01.00]C:\\new\\recording"), 5000)[0].Text == @"C:\new\recording",
            "literal backslashes in lyric text are preserved");

        string embedded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"content":[{"type":1,"lyricContent":[["译文一"],["译文二"]]}],"version":1}"""));
        var krc = parser.Import("[language:" + embedded + "]\r[1000,1000]<0,1000,0>A\r[2000,1000]<0,1000,0>B");
        var krcLines = parser.Parse(krc, 5000);
        check(krcLines.Length == 2 && krcLines[0].Translation == "译文一" && krcLines[1].Translation == "译文二" &&
            !krc.Original.Content.Contains("[language:", StringComparison.Ordinal),
            "CR KRC translation extraction removes only its metadata row");
    }

    public static async Task CheckPlaybackAsync(LyricsRefreshService resolver,
        LyricsRepository repository, SQLiteAsyncConnection database, string folder, Action<bool, string> check)
    {
        string content = Sample.ReplaceLineEndings("\r");
        string path = Path.Combine(folder, "cr-sidecar.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), content);
        var displayed = await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromMilliseconds(DurationMs) }, default);
        check(displayed.Count == SampleLineCount && displayed[9].StartMs == 25190 && displayed[^1].StartMs == 238950,
            "real CR-only sidecar file resolves to 65 playback display rows");

        var oldDocument = new LyricsDocument(new(content, LyricsFormat.EnhancedLrc), null);
        string cachedPath = Path.Combine(folder, "cr-cached.flac");
        OneShotLyricsCache.Save(cachedPath, oldDocument);
        displayed = await resolver.SetLyrics(new() { Path = cachedPath, Duration = TimeSpan.FromMilliseconds(DurationMs) }, default);
        check(displayed.Count == SampleLineCount && OneShotLyricsCache.Load(cachedPath)?.Document == oldDocument,
            "real existing JSON cache displays every row without rewriting its original text");

        await database.ExecuteAsync("INSERT INTO Music(Id) VALUES(3)");
        var snapshot = await repository.GetAsync(3);
        check(await repository.SaveAsync(3, oldDocument, snapshot.Revision, "User"), "store old CR document in real SQLite");
        displayed = await resolver.SetLyrics(new() { Id = 3, Path = Path.Combine(folder, "cr-stored.flac"),
            Duration = TimeSpan.FromMilliseconds(DurationMs) }, default);
        check(displayed.Count == SampleLineCount && (await repository.GetAsync(3)).Document == oldDocument,
            "real existing SQLite document displays every row without migration or refetch");
        await database.RunInTransactionAsync(connection => LyricsRepository.DeleteInTransaction(connection, 3));
        await database.ExecuteAsync("DELETE FROM Music WHERE Id=3");
    }

    private static bool MatchesSample(ImmutableArray<ParsedLyricLine> lines)
    {
        if (lines.Length != SampleLineCount) return false;
        int index = 0;
        foreach (var raw in Sample.AsSpan().EnumerateLines())
        {
            var expected = raw.Trim();
            double start = int.Parse(expected.Slice(1, 2), CultureInfo.InvariantCulture) * 60000 +
                int.Parse(expected.Slice(4, 2), CultureInfo.InvariantCulture) * 1000 +
                int.Parse(expected.Slice(7, 2), CultureInfo.InvariantCulture) * 10;
            var line = lines[index];
            double end = index + 1 < lines.Length ? lines[index + 1].StartMs : DurationMs;
            if (Math.Abs(line.StartMs - start) > 0.001 || line.EndMs != end ||
                line.Text != expected[10..].Trim().ToString() || !line.Words.IsEmpty)
                return false;
            index++;
        }
        return index == SampleLineCount;
    }
}
