using System.Buffers.Binary;
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
        Check(ToolUtils.Reads == before + 1 && first.Length == 2 && first.All(track => track.BitDepth == 16) && first[0].EndSample == 48_000 &&
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

    internal static async Task CheckAdditionalFormatsAsync(string fixture)
    {
        // The encoded fixtures use synthetic mono PCM pulses at 48 kHz. Each lossless
        // fixture has 144137 samples, including a partial final packet.
        const int samples = 144_137;
        string directory = Directory.CreateDirectory(Path.Combine(fixture, "CUE source formats")).FullName;
        string alac = Path.Combine(directory, "整轨.m4a");
        string aiff = Path.Combine(directory, "disc.aiff");
        string aifc = Path.Combine(directory, "disc.aifc");
        string floatAifc = Path.Combine(directory, "float64.aifc");
        string wave = Path.Combine(directory, "packed20.wav");
        WriteAiff(aiff, samples, ""u8, 20);
        WriteAiff(aifc, samples, "sowt"u8);
        WriteAiff(floatAifc, samples, "fl64"u8, 64);
        WriteWave(wave, samples, 24, validBits: 20);
        string flac = CopyFixture("Packed20.flac", "packed20.flac");
        byte[] editedAlac = File.ReadAllBytes(FixturePath("Image.m4a"));
        int editList = editedAlac.AsSpan().IndexOf("elst"u8);
        Check(editList >= 4 && editedAlac[editList + 4] == 0, "ALAC fixture contains a version-zero edit list");
        BinaryPrimitives.WriteUInt32BigEndian(editedAlac.AsSpan(editList + 12), 2000);
        BinaryPrimitives.WriteUInt32BigEndian(editedAlac.AsSpan(editList + 16), 4800);
        File.WriteAllBytes(alac, editedAlac);
        string ape = CopyFixture("Image.ape", "disc.ape");
        string wv = CopyFixture("Image.wv", "disc.wv");
        string packedWv = CopyFixture("Packed20.wv", "packed20.wv");
        string floatWv = CopyFixture("Float32.wv", "float32.wv");
        string aac = CopyFixture("Lossy.m4a", "lossy.m4a");
        string hybrid = CopyFixture("Hybrid.wv", "hybrid.wv");
        string unknown = Path.Combine(directory, "unknown.wv");
        byte[] wavPackHeader = File.ReadAllBytes(wv);
        BinaryPrimitives.WriteUInt32LittleEndian(wavPackHeader.AsSpan(12), uint.MaxValue);
        File.WriteAllBytes(unknown, wavPackHeader);
        string truncated = Path.Combine(directory, "truncated.aif");
        File.WriteAllBytes(truncated, File.ReadAllBytes(aiff)[..^2]);
        string compressed = Path.Combine(directory, "compressed.aifc");
        WriteAiff(compressed, samples, "ulaw"u8);
        string unsupportedApe = Path.Combine(directory, "unsupported.ape");
        byte[] apeHeader = File.ReadAllBytes(ape);
        int apeDescriptorLength = BinaryPrimitives.ReadInt32LittleEndian(apeHeader.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(apeHeader.AsSpan(apeDescriptorLength + 16), 32);
        File.WriteAllBytes(unsupportedApe, apeHeader);

        (string Path, int BitDepth)[] accepted = [(aiff, 20), (aifc, 16), (floatAifc, 64), (wave, 20), (flac, 20),
            (alac, 16), (ape, 16), (wv, 16), (packedWv, 20), (floatWv, 32)];
        string[] rejected = [aac, hybrid, unknown, truncated, compressed, unsupportedApe];
        foreach (string path in accepted.Select(entry => entry.Path).Concat(rejected))
            WriteSheet(path + ".cue", Path.GetFileName(path), "First", "Last", "00:01:01");

        var database = new MusicDatabaseService(Path.Combine(fixture, "cue-formats.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlayListMusic>();
        await database.CheckFolderBeforeAdd(new StorageFolder(directory));
        foreach (var (path, bitDepth) in accepted)
        {
            var tracks = (await database.GetMusicByPhysicalPathAsync(path)).OrderBy(track => track.CueTrackNo).ToArray();
            Check(tracks.Length == 2 && tracks.All(track => track.IsCueTrack && track.SampleRate == 48_000
                && track.BitDepth == bitDepth && track.EncodingKind == 1)
                && tracks[0].StartSample == 0 && tracks[0].EndSample == 48_640
                && tracks[1].StartSample == 48_640 && tracks[1].EndSample == samples,
                $"{Path.GetFileName(path)} keeps exact sample boundaries and {bitDepth}-bit lossless encoding metadata");
            if (path == alac)
                Check(tracks.All(track => track.CodecId == "ALAC"), "the physical ALAC codec is available to version sorting");
        }
        foreach (string path in rejected)
        {
            var tracks = await database.GetMusicByPhysicalPathAsync(path);
            Check(tracks.Count == 1 && tracks[0] is { IsCueTrack: false, IsCueContainer: false, BitDepth: 48, EncodingKind: 2 },
                $"{Path.GetFileName(path)} keeps its ordinary source row");
            if (path == aac)
                Check(tracks[0].CodecId == "MPEG-4", "AAC M4A retains its ordinary codec label");
        }
        int reads = ToolUtils.Reads;
        Check(await database.ScanChangedFolderAsync(directory) == 0 && ToolUtils.Reads == reads,
            "rescan reuses exact accepted mappings and does not reread music metadata");

        string CopyFixture(string name, string destination)
        {
            string path = Path.Combine(directory, destination);
            File.Copy(FixturePath(name), path);
            return path;
        }

        static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    }

    private static void WriteAiff(string path, int samples, ReadOnlySpan<byte> compression, short bits = 16)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int bytes = samples * ((bits + 7) / 8);
        int paddedBytes = bytes + (bytes & 1);
        bool aifc = !compression.IsEmpty;
        writer.Write("FORM"u8);
        WriteInt32(46 + paddedBytes + (aifc ? 18 : 0));
        writer.Write(aifc ? "AIFC"u8 : "AIFF"u8);
        if (aifc)
        {
            writer.Write("FVER"u8);
            WriteInt32(4);
            WriteInt32(unchecked((int)0xA2805140));
        }
        writer.Write("COMM"u8);
        WriteInt32(aifc ? 24 : 18);
        WriteInt16(1);
        WriteInt32(samples);
        WriteInt16(bits);
        writer.Write(new byte[] { 0x40, 0x0E, 0xBB, 0x80, 0, 0, 0, 0, 0, 0 }); // 48000 as an 80-bit extended float.
        if (aifc)
        {
            writer.Write(compression);
            WriteInt16(0); // Empty, padded Pascal string for the compression name.
        }
        writer.Write("SSND"u8);
        WriteInt32(8 + bytes);
        writer.Write(0L);
        writer.BaseStream.SetLength(writer.BaseStream.Position + paddedBytes);

        void WriteInt32(int value) => writer.Write(BinaryPrimitives.ReverseEndianness(value));
        void WriteInt16(short value) => writer.Write(BinaryPrimitives.ReverseEndianness(value));
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

    private static void WriteWave(string path, int samples, ushort bits = 16, ushort validBits = 0)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int blockAlign = bits / 8;
        int bytes = samples * blockAlign;
        int paddedBytes = bytes + (bytes & 1);
        int formatBytes = validBits == 0 ? 16 : 40;
        writer.Write("RIFF"u8);
        writer.Write(20 + formatBytes + paddedBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(formatBytes);
        writer.Write((ushort)(validBits == 0 ? 1 : 0xFFFE));
        writer.Write((short)1);
        writer.Write(48_000);
        writer.Write(48_000 * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write(bits);
        if (validBits != 0)
        {
            writer.Write((short)22);
            writer.Write(validBits);
            writer.Write(4); // Mono center channel.
            writer.Write(new Guid("00000001-0000-0010-8000-00AA00389B71").ToByteArray());
        }
        writer.Write("data"u8);
        writer.Write(bytes);
        writer.BaseStream.SetLength(writer.BaseStream.Position + paddedBytes);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL CUE folders: " + message);
        Console.WriteLine("PASS CUE folders: " + message);
    }
}
