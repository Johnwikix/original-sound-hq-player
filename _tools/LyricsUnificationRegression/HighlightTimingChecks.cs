using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

/// <summary>Checks display intervals through the production parser, file resolver and SQLite migration.</summary>
internal static class HighlightTimingChecks
{
    internal static async Task RunAsync(LyricsParser parser, LyricsRefreshService resolver,
        LyricsRepository repository, SQLiteAsyncConnection database, string folder, Action<bool, string> check)
    {
        const string qrc = "[77396,6279]on(80963,2712)\n[96652,2242]Drag(96652,2242)";
        const string krc = "[77396,6279]<3567,2712,0>on\n[96652,2242]<0,2242,0>Drag";
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
            <p begin="1s" end="3s"><span begin="1s" end="3s">A</span></p>
            <p begin="1s" end="2s"><span begin="1s" end="2s">B</span></p>
            <p begin="2.5s" end="4s"><span begin="2.5s" end="4s">Overlap</span></p>
            <p begin="4.5s" end="5s">Next</p>
            </div></body></tt>
            """;
        foreach (var (extension, content) in new[] { ("qrc", qrc), ("krc", krc), ("ttml", ttml) })
        {
            string path = Path.Combine(folder, "highlight-" + extension + ".flac");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, extension), content);
            var source = parser.Parse(parser.Import(content), 120000);
            var displayed = await resolver.SetLyrics(new() { Path = path, Duration = TimeSpan.FromSeconds(120) }, default);
            check(displayed.Count == source.Length && displayed.Select((line, i) =>
                line.StartMs == source[i].StartMs && line.EndMs == source[i].EndMs &&
                (source[i].Words.IsEmpty || line.Words.Select((word, j) =>
                    word.StartMs == source[i].Words[j].StartMs &&
                    word.StartMs + word.DurationMs == source[i].Words[j].EndMs).All(value => value))).All(value => value),
                extension + ": display retention leaves all source and word times unchanged");
            if (extension != "ttml")
                check(displayed[0].EndMs == 83675 && displayed[0].HighlightEndMs == 96652,
                    extension + ": Unchained's 12977 ms gap retains the completed row until the next entrance");
            else
                check(displayed[0].HighlightEndMs == 3000 && displayed[1].HighlightEndMs == 2500 &&
                    displayed[2].HighlightEndMs == 4500,
                    "TTML: duplicate starts share a distinct next entrance without truncating overlap");
            check(displayed[^1].HighlightEndMs == 122000,
                extension + ": the last row retains the legacy song-duration display boundary");
            check(File.ReadAllText(Path.ChangeExtension(path, extension)) == content,
                extension + ": displaying a gap does not rewrite the original sidecar");
        }

        string shortPath = Path.Combine(folder, "highlight-short.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(shortPath, ".qrc"),
            "[1000,200]A(1000,200)\n[1200,300]B(1200,300)\n[1500,100]C(1500,100)");
        var shortLines = await resolver.SetLyrics(new() { Path = shortPath, Duration = TimeSpan.Zero }, default);
        check(shortLines[0].HighlightEndMs == 1200 && shortLines[1].HighlightEndMs == 1500 &&
            shortLines[^1].HighlightEndMs == 1600 && shortLines[0].Words[0].DurationMs == 200,
            "short consecutive rows and an unknown song duration retain their real boundaries");

        await database.ExecuteAsync("INSERT INTO Music(Id) VALUES(4)");
        try
        {
            await database.InsertAsync(new MusicLyrics { MusicId = 4, Krc = qrc });
            var snapshot = await repository.GetAsync(4);
            var displayed = await resolver.SetLyrics(new() { Id = 4, Path = Path.Combine(folder, "highlight-stored.flac"),
                Duration = TimeSpan.FromSeconds(120) }, default);
            check(displayed[0].EndMs == 83675 && displayed[0].HighlightEndMs == 96652 &&
                (await repository.GetAsync(4)) == snapshot && (await database.FindAsync<MusicLyrics>(4)).Krc == qrc,
                "real SQLite migration displays the gap without changing either legacy or V2 lyrics");
        }
        finally
        {
            await database.RunInTransactionAsync(connection => LyricsRepository.DeleteInTransaction(connection, 4));
            await database.ExecuteAsync("DELETE FROM Music WHERE Id=4");
        }
    }
}
