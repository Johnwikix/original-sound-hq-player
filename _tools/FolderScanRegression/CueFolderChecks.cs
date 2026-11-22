using System.Text;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using Windows.Storage;

internal static class CueFolderChecks
{
    internal static async Task CheckSweetIcesCreamAsync(string path)
    {
        long[] starts = [0, 11_186_112, 20_996_304, 29_364_720, 40_085_136, 49_006_272, 61_243_728, 72_074_688, 84_443_856, 94_518_159];
        var tracks = await AddFolderService.ReadCueTracksAsync(path);
        Check(tracks.Count == 9 && tracks.Select(track => track.StartSample).SequenceEqual(starts[..^1]) &&
            tracks.Select(track => track.EndSample).SequenceEqual(starts[1..]) &&
            tracks.All(track => track.Album == "Sweet Ices Cream" && track.Author == "Kano" && track.Genre == "J-Pop / Utaite / Doujin" && track.Year == 2011) &&
            tracks[0].Title == "小夜子" && tracks[^1].Title == "マッシュルームマザー",
            "Sweet Ices Cream real FLAC has the nine reference sample ranges and CUE tags");
    }

    internal static async Task RunAsync(string fixture)
    {
        string directory = Directory.CreateDirectory(Path.Combine(fixture, "CUE [disc]")).FullName;
        string source = Path.Combine(directory, "歌 [mirror].wav");
        string cue = Path.Combine(directory, "disc.cue");
        string renamedCue = Path.Combine(directory, "renamed.cue");
        int sampleCount = 144_137;
        WriteWave(source, sampleCount);
        var database = new MusicDatabaseService(Path.Combine(fixture, "cue.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlayListMusic>();
        database.Versions = new(database.Connection);
        await database.Versions.InitializeAsync();
        await database.CheckFolderBeforeAdd(new StorageFolder(directory));
        Music whole = (await database.GetMusicListAsync()).Single();
        await database.Connection.InsertAsync(new PlayListMusic { MusicId = whole.Id, SongId = whole.SongId });
        WriteSheet(cue, Path.GetFileName(source), "同じ", "同じ", "00:01:00");
        int before = ToolUtils.Reads;
        await database.ScanChangedFolderAsync(directory);
        var first = (await database.GetMusicByPhysicalPathAsync(source)).Where(track => track.IsCueTrack).OrderBy(track => track.CueTrackNo).ToArray();
        Check(ToolUtils.Reads == before + 1 && first.Length == 2 && first[0].EndSample == 48_000 &&
            first[1].StartSample == 48_000 && first[1].EndSample == sampleCount && first[1].Year == 2011 &&
            first[0].Genre == "J-Pop / Utaite / Doujin" && first[1].Duration == TimeSpan.FromTicks((sampleCount - 48_000L) * TimeSpan.TicksPerSecond / 48_000),
            "one exact source probe supplies both tracks, REM fields and the final sample");
        Check((await database.FindMusicByPathAsync(source)) is { IsCueContainer: true, CanEditFile: false } hidden && hidden.Id == whole.Id &&
            (await database.GetFoldersWithSongCountsAsync()).Single().SongCount == 2 &&
            (await database.Connection.Table<PlayListMusic>().FirstAsync()).MusicId == whole.Id && first[0].SongId != first[1].SongId,
            "expansion hides the full source from counts and retains playlist and independent sibling identities");

        using (var activation = new OneShotPlaybackService(new(), new(), database, new(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OneShotPlaybackService>.Instance))
        {
            activation.PlayNow(cue);
            var pending = typeof(OneShotPlaybackService).GetField("_resolveTask",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var opened = await ((Task<IReadOnlyList<Music>>)pending.GetValue(activation)!).WaitAsync(TimeSpan.FromSeconds(5));
            Check(opened.Select(track => track.Id).SequenceEqual(first.Select(track => track.Id)) &&
                activation.TryGetResolvedMusic()?.Id == first[0].Id, "external CUE open resolves the complete ordered library queue and its startup track");
            activation.PlayNow(source);
            var physical = await ((Task<IReadOnlyList<Music>>)pending.GetValue(activation)!).WaitAsync(TimeSpan.FromSeconds(5));
            Check(physical.Count == 1 && physical[0].Id == whole.Id && physical[0].IsCueContainer,
                "opening the physical source still resolves the whole version");
        }
        byte[] originalAudio = await File.ReadAllBytesAsync(source);
        foreach (Music protectedVersion in new[] { first[0], whole })
        {
            bool blocked = false;
            try { await database.QueueMetadataWriteAsync(protectedVersion, null, LyricsDocument.Empty, 0, lyricsChanged: false); }
            catch (InvalidOperationException e) when (e.Message == ToolUtils.GetString("CueSharedSource")) { blocked = true; }
            Check(blocked && await database.Connection.Table<PendingMetadataWrite>().CountAsync() == 0,
                "both an active CUE child and a stale whole-file editor are prevented from queueing image writes");
        }
        byte[] afterBlockedWrites = await File.ReadAllBytesAsync(source);
        Check(originalAudio.AsSpan().SequenceEqual(afterBlockedWrites), "blocked metadata writes leave the entire physical image intact");

        await database.Connection.ExecuteAsync("UPDATE Music SET ImageHash='old' WHERE Id=?", first[0].Id);
        await database.Connection.ExecuteAsync("UPDATE Music SET ImageHash='custom',CoverOverridePath='version.bin' WHERE Id=?", first[1].Id);
        await File.WriteAllTextAsync(Path.Combine(directory, "cover.jpg"), "new revision");
        Check(await database.ReconcileLocalCoversAsync(directory, default) &&
            (await database.Connection.GetAsync<Music>(first[0].Id)).ImageHash == "" &&
            (await database.Connection.GetAsync<Music>(first[1].Id)).ImageHash == "custom",
            "directory replacement invalidates inherited images while keeping a sibling's manual cover");
        Check(!await database.UpdateCoverSourceAsync(first[0], "", "", "", "late", 0, 1),
            "a late old-cover read cannot overwrite the new directory revision");

        before = ToolUtils.Reads;
        Check(await database.ScanChangedFolderAsync(directory) == 0 && ToolUtils.Reads == before, "unchanged CUE does not reopen the audio source");
        await database.Connection.RunInTransactionAsync(db =>
        {
            Music item = db.Get<Music>(first[0].Id);
            var original = db.Get<Music>(first[0].Id);
            item.Title = "用户标题";
            item.Genre = "";
            CueMetadataOverrides.Capture(db, original, item);
            db.Update(item);
        });
        WriteSheet(cue, Path.GetFileName(source), "元の名前", "同じ", "00:01:01");
        await database.ScanChangedFolderAsync(directory);
        var edited = await database.FindCueTrackAsync(cue, 1);
        var shifted = await database.FindCueTrackAsync(cue, 2);
        Check(edited?.Id == first[0].Id && edited.Title == "用户标题" && edited.Genre == "" &&
            shifted?.Id == first[1].Id && shifted.StartSample == 48_640, "source edits preserve mapped IDs and explicit metadata overrides");

        File.Move(cue, renamedCue);
        var changes = new WatchedFolderChanges();
        changes.AddFile(renamedCue, cue);
        await AutoRescanService.AutoScan(database, new(StringComparer.OrdinalIgnoreCase) { [directory] = changes }, default);
        Check((await database.FindCueTrackAsync(renamedCue, 1))?.Id == first[0].Id && await database.FindCueTrackAsync(cue, 1) is null,
            "watcher retains CUE identity across a rename");
        byte[] valid = File.ReadAllBytes(renamedCue);
        File.WriteAllText(renamedCue, "FILE \"missing.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00");
        await database.ScanChangedFolderAsync(directory);
        Check((await database.FindCueTrackAsync(renamedCue, 1))?.Id == first[0].Id &&
            (await database.FindMusicByPathAsync(source))!.IsCueContainer, "a broken replacement retains the last successful mapping");

        File.Delete(renamedCue);
        await database.ScanChangedFolderAsync(directory);
        Check((await database.Connection.GetAsync<Music>(first[0].Id)).IsMissing &&
            (await database.FindMusicByPathAsync(source)) is { IsCueContainer: false } restored && restored.Id == whole.Id,
            "confirmed removal retires child rows and restores the legacy source");
        File.WriteAllBytes(renamedCue, valid);
        await database.ScanChangedFolderAsync(directory);
        Check((await database.FindCueTrackAsync(renamedCue, 1))?.Id == first[0].Id &&
            (await database.FindCueTrackAsync(renamedCue, 2))?.Id == first[1].Id, "restoring a sheet reactivates its original child rows");

        string duplicate = Path.Combine(directory, "copy.cue");
        File.Copy(renamedCue, duplicate);
        var alias = await database.ImportCueAsync(duplicate);
        Check(alias.Select(track => track.Id).SequenceEqual(first.Select(track => track.Id)), "an identical CUE mapping deduplicates on external open");
        File.Delete(duplicate);
        WriteSheet(renamedCue, Path.GetFileName(source), "元の名前", "別の曲", "00:01:02");
        await database.ScanChangedFolderAsync(directory);
        Check((await database.FindCueTrackAsync(renamedCue, 2))?.Id != first[1].Id &&
            (await database.Connection.GetAsync<Music>(first[1].Id)).IsMissing, "uncertain same-number replacement gets a new ID while old links stay resolvable");

        string source2 = Path.Combine(directory, "第二.wav");
        WriteWave(source2, 58_123);
        string multi = Path.Combine(directory, "multiple.cue");
        File.WriteAllText(multi, $"""
            TITLE "Import"
            PERFORMER "Artist"
            FILE "{Path.GetFileName(source)}" WAVE
                TRACK 01 AUDIO
                    PREGAP 00:01:00
                    INDEX 00 00:00:00
                    INDEX 01 00:00:03
            FILE "{Path.GetFileName(source2)}" WAVE
                TRACK 02 AUDIO
                    INDEX 01 00:00:00
                    POSTGAP 00:02:00
            """, Encoding.Unicode);
        var multiTracks = await AddFolderService.ReadCueTracksAsync(multi);
        Check(multiTracks.Count == 2 && multiTracks[0].StartSample == 1920 && multiTracks[0].EndSample == sampleCount &&
            multiTracks[1].StartSample == 0 && multiTracks[1].EndSample == 58_123 && multiTracks[0].TitleFromFileName == true,
            "UTF-16 multi-FILE tracks reset position, preserve real gaps and skip synthetic gaps");
        Check((await database.ImportCueAsync(multi)).Count == 0 && (await database.FindCueTrackAsync(renamedCue, 1))?.Id == first[0].Id,
            "a conflicting CUE preserves the previously verified mapping");

        string fresh = Directory.CreateDirectory(Path.Combine(directory, "Fresh")).FullName;
        string freshSource = Path.Combine(fresh, "fresh.wav");
        string freshCue = Path.Combine(fresh, "fresh.cue");
        WriteWave(freshSource, 100_000);
        WriteSheet(freshCue, "fresh.wav", "First", "Second", "00:01:00");
        await database.ScanChangedFolderAsync(directory);
        var freshTracks = await database.GetMusicByPhysicalPathAsync(freshSource);
        Check(freshTracks.Count == 2 && freshTracks.All(track => track.IsCueTrack), "a fresh split CUE skips the ordinary duplicate");
        File.Delete(freshCue);
        using (AudioFileWriteGate.BeginWrite(freshSource))
        {
            await database.ScanChangedFolderAsync(directory);
            Check((await database.GetMusicByPhysicalPathAsync(freshSource)).All(track => !track.IsMissing),
                "a busy source delays retirement until a replacement whole row is ready");
        }
        await database.ScanChangedFolderAsync(directory);
        var afterRemoval = await database.GetMusicByPhysicalPathAsync(freshSource);
        Check(afterRemoval.Count == 3 && afterRemoval.Count(track => !track.IsMissing && !track.IsCueTrack) == 1 &&
            afterRemoval.Where(track => track.IsCueTrack).All(track => track.IsMissing), "retirement creates the replacement whole in one commit and keeps child history");
    }

    private static void WriteSheet(string path, string source, string first, string second, string secondStart) => File.WriteAllText(path, $"""
        REM DATE 2011
        REM GENRE "J-Pop / Utaite / Doujin"
        TITLE "Sample"
        PERFORMER "Kano"
        FILE "{source}" WAVE
            TRACK 01 AUDIO
                TITLE "{first}"
                INDEX 01 00:00:00
            TRACK 02 AUDIO
                TITLE "{second}"
                INDEX 00 00:00:74
                INDEX 01 {secondStart}
        """);

    private static void WriteWave(string path, int samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(48_000);
        writer.Write(96_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL CUE folders: " + message);
        Console.WriteLine("PASS CUE folders: " + message);
    }
}
