using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 2 && args[0] == "--verify-publish")
{
    using var file = File.OpenRead(args[1]);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    var required = new Dictionary<string, string[]>
    {
        ["WinUIMusicPlayer.ViewModel.MusicDetailsViewModel"] = ["get_Editor", "get_Versions", "get_SelectedVersion", "set_SelectedVersion", "get_SetPreferredCommand", "get_LinkVersionCommand", "get_SplitVersionCommand", "get_RemoveMissingVersionCommand", "get_UndoVersionCommand", "get_VersionDraftHint"],
        ["WinUIMusicPlayer.ViewModel.MusicVersionItem"] = ["get_TechnicalInfo", "get_Path", "get_StateText", "RowOpacity"],
        ["WinUIMusicPlayer.Model.Music"] = ["get_TitleFromFileName", "set_TitleFromFileName", "get_MetadataRevision"],
        ["WinUIMusicPlayer.Services.SongVersionStore"] = ["ReadLibraryAsync", "GetCandidatesAsync", "IndexMemberTags", "RemoveMissingVersionAsync"],
        ["WinUIMusicPlayer.State.PlaybackQueueState"] = ["SequentialEntryIdAt", "Replace", "InsertNext"],
        ["WinUIMusicPlayer.Model.SongRecord"] = ["get_Id", "set_Id", "get_PreferredMusicId", "set_PreferredMusicId", "get_IsFavorite"],
        ["WinUIMusicPlayer.Model.SongLyricsRecord"] = ["get_SongId", "set_SongId", "get_Revision", "set_Revision"],
        ["WinUIMusicPlayer.Model.VersionLyricsOverrideRecord"] = ["get_MusicId", "set_MusicId"],
        ["WinUIMusicPlayer.Model.SongVersionOperationRecord"] = ["get_BeforeJson", "set_BeforeJson", "get_AfterJson", "set_AfterJson"],
        ["WinUIMusicPlayer.Model.SongVersionSchemaRecord"] = ["get_Version", "set_Version"],
        ["WinUIMusicPlayer.Model.SongAutoLinkRecord"] = ["get_SongId", "set_SongId", "get_MatchKey", "set_MatchKey", "get_IsExcluded", "set_IsExcluded"],
        ["WinUIMusicPlayer.Model.SongAutoLinkDirtyRecord"] = ["get_SongId", "set_SongId"],
        ["WinUIMusicPlayer.Model.SongAutoLinkPendingRecord"] = ["get_MatchKey", "set_MatchKey"],
        ["WinUIMusicPlayer.View.Controls.MusicDetailControl"] = ["InitializeComponent"],
        ["WinUIMusicPlayer.View.Controls.MusicVersionDialogControl"] = ["InitializeComponent"],
        ["WinUIMusicPlayer.View.MusicDetailsPanel"] = ["Open"],
        ["WinUIMusicPlayer.View.SubView.MusicDetailsWindow"] = [".ctor"]
    };
    bool latestPlaylistIntent = false;
    foreach (var handle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        if (name == "WinUIMusicPlayer.Services.MusicCommands")
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name) != "PlayPlaylistEntryAsync") continue;
                var signature = metadata.GetBlobReader(method.Signature);
                var header = signature.ReadSignatureHeader();
                if (header.IsGeneric) signature.ReadCompressedInteger();
                latestPlaylistIntent = signature.ReadCompressedInteger() == 4;
            }
        if (!required.TryGetValue(name, out var members)) continue;
        var present = new HashSet<string>(type.GetMethods().Select(method => metadata.GetString(metadata.GetMethodDefinition(method).Name)));
        foreach (string member in members) if (!present.Contains(member)) throw new InvalidOperationException("Trimmed member missing: " + name + "." + member);
        required.Remove(name);
        Console.WriteLine("PASS trimmed " + name);
    }
    if (required.Count != 0 || !latestPlaylistIntent) throw new InvalidOperationException("Published assembly is incomplete or predates the final playlist intent change: " + string.Join(',', required.Keys));
    Console.WriteLine("PASS published version commands, SQLite models, both detail hosts, and final playlist intent retained");
    return;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
async Task Conflict(Func<Task> action, string name)
{
    try { await action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new InvalidOperationException("Expected conflict: " + name);
}
string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "SongVersionRegression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var database = new SQLiteAsyncConnection(Path.Combine(root, "library.db"));
try
{
    await MigrationBackupChecks.RunAsync(root, Check);
    await AutomaticChecks.RunAsync(root, Check);
    await database.CreateTableAsync<Music>();
    await database.CreateTableAsync<PlayListMusic>();
    await database.CreateTableAsync<MusicLyrics>();
    var legacy = new SQLiteAsyncConnection(Path.Combine(root, "old-library.db"));
    try
    {
        await legacy.ExecuteAsync("CREATE TABLE Music (Id INTEGER PRIMARY KEY AUTOINCREMENT,Title TEXT,Author TEXT,Album TEXT,Path TEXT,IsFavorite INTEGER,[Order] INTEGER)");
        await legacy.ExecuteAsync("INSERT INTO Music VALUES(41,'Old title','Old artist','Old album','old.flac',1,8)");
        await legacy.ExecuteAsync("CREATE TABLE PlayListMusic (Id INTEGER PRIMARY KEY AUTOINCREMENT,PlayListId INTEGER,MusicId INTEGER,[Order] INTEGER)");
        await legacy.ExecuteAsync("INSERT INTO PlayListMusic VALUES(91,3,41,17)");
        await legacy.CreateTableAsync<Music>();
        await legacy.CreateTableAsync<PlayListMusic>();
        await legacy.CreateTableAsync<MusicLyrics>();
        await legacy.InsertAsync(new MusicLyrics { MusicId = 41, Lyrics = "[00:01.00]Old lyrics" });
        var oldStore = new SongVersionStore(legacy);
        await oldStore.InitializeAsync();
        var oldFile = await legacy.FindAsync<Music>(41);
        Check(oldFile.SongId > 0 && oldFile.CodecId == "" && oldFile.MetadataRevision == 0 && !oldFile.IsMissing,
            "real old schema upgrades nullable added columns safely");
        Check((await legacy.FindAsync<PlayListMusic>(91)).MusicId == 41 && (await legacy.FindAsync<PlayListMusic>(91)).Order == 17,
            "old playlist keeps its original version, entry id and position");
        var oldLyrics = new LyricsRepository(legacy, new());
        await oldLyrics.InitializeAsync();
        Check((await oldLyrics.GetAsync(41)).Document.Original.Content.Contains("Old lyrics"), "old lyrics migrate into the new song without losing content");
        Check((await oldStore.GetSongAsync(41))!.IsFavorite, "old favorites migrate to song ownership");
        await legacy.InsertOrReplaceAsync(new SongVersionSchemaRecord { Version = 99 });
        await Conflict(() => oldStore.InitializeAsync(), "future schema refuses to initialize");
    }
    finally { await legacy.CloseAsync(); }
    var flac = new Music { Title = "Song", Author = "Artist", Album = "Album", Path = "a.flac", Extension = "FLAC", CodecId = "flac", EncodingKind = 1, SourceId = 4, Genre = "Pop", Year = 2010 };
    var mp3 = new Music { Title = "Song", Author = "Artist", Album = "Album", Path = "b.mp3", Extension = "MP3", CodecId = "mp3", EncodingKind = 2, Genre = "Rock", Year = 2011, IsFavorite = true, Order = 7 };
    var mp3Low = new Music { Id = 99, CodecId = "mp3", EncodingKind = 2, BitRate = 128 };
    mp3.BitRate = 320;
    await database.InsertAsync(flac);
    await database.InsertAsync(mp3);
    await database.InsertAsync(new PlayListMusic { PlayListId = 1, MusicId = flac.Id, Order = 9 });
    await database.InsertAsync(new PlayListMusic { PlayListId = 1, MusicId = mp3.Id, Order = 3 });
    var store = new SongVersionStore(database);
    await store.InitializeAsync();
    var lyrics = new LyricsRepository(database, new LyricsParser());
    await lyrics.InitializeAsync();
    Check(await database.Table<SongRecord>().CountAsync() == 2, "schema phase preserves version identities before the automatic pass");
    await store.InitializeAsync();
    Check(await database.Table<SongRecord>().CountAsync() == 2, "migration is idempotent");
    Check((await database.FindAsync<Music>(flac.Id)).SongId > 0, "original version identity retained");
    Check((await database.FindAsync<PlayListMusic>(1)).SongId > 0, "playlist entry migrated");
    Check(SongVersionPolicy.Matches(flac, mp3), "optional metadata differences are candidates");
    Check(!SongVersionPolicy.Matches(flac, new Music { Title = "Song", Author = "Artist", Album = "Other" }), "different albums remain separate");
    Check(!SongVersionPolicy.Matches(flac, new Music { Title = "Song (Remastered)", Author = "Artist", Album = "Album" }), "remaster suffix is preserved");
    Check(SongVersionPolicy.Order([mp3, flac])[0].Id == mp3.Id, "local audio precedes uncached remote lossless");
    Check(SongVersionPolicy.Order([mp3Low, mp3])[0].Id == mp3.Id, "same codec bitrate ordering");
    Check(SongVersionPolicy.Order([mp3, flac], flac.Id)[0].Id == flac.Id, "explicit remote preference precedes local automatic choice");
    var cd = new Music { Id = 10, EncodingKind = 1, Channel = 2, BitDepth = 16, SampleRate = 44100, CodecId = "flac" };
    var studio = new Music { Id = 11, EncodingKind = 1, Channel = 2, BitDepth = 24, SampleRate = 96000, CodecId = "pcm" };
    var sameDepth = new Music { Id = 12, EncodingKind = 1, Channel = 2, BitDepth = 24, SampleRate = 48000, CodecId = "flac" };
    Check(SongVersionPolicy.Order([cd, studio, sameDepth]).SequenceEqual([studio, sameDepth, cd]) &&
        SongVersionPolicy.Order([sameDepth, studio, cd]).SequenceEqual([studio, sameDepth, cd]), "PCM depth and rate are stable across containers and input order");
    var cached = new Music { Id = 17, SourceId = 5, EncodingKind = 2, IsRemoteCached = true };
    Check(SongVersionPolicy.Order([flac, cached, mp3], forPlayback: true).SequenceEqual([mp3, cached, flac]), "playback orders local, complete cache, then online");
    mp3.IsMissing = true;
    Check(SongVersionPolicy.Order([mp3, flac])[0] == flac, "available remote beats missing local for automatic display");
    Check(SongVersionPolicy.Order([mp3, flac], mp3.Id)[0] == mp3, "a manually pinned missing file remains identifiable");
    cached.IsMissing = true;
    Check(SongVersionPolicy.Order([mp3, cached, flac], forPlayback: true)[0] == cached, "a complete cache can play even after its origin disappears");
    Check(SongVersionPolicy.Order([mp3, cached])[0] == cached, "all origins missing still shows the version with a complete cache");
    mp3.IsMissing = false;
    var dsd = new Music { Id = 9, EncodingKind = 1, Channel = 2, BitDepth = 1, SampleRate = 2822400, CodecId = "DSD" };
    Check(SongVersionPolicy.Order([studio, dsd, cd]).SequenceEqual([dsd, studio, cd]), "DSD and PCM keep independent comparison groups");
    Check(SongVersionPolicy.Matches(flac, new Music { Title = " Ｓｏｎｇ ", Author = "artist", Album = "Album" }), "candidate normalization preserves Unicode compatibility and ignores outer whitespace");
    Check(!SongVersionPolicy.Matches(flac, new Music { Title = "Song (Live)", Author = "Artist", Album = "Album" }), "live qualifier never disappears from matching");
    Check(SongVersionPolicy.Matches(new Music { Title = "Unknown Pleasures", Author = "Unknown Mortal Orchestra", Album = "Album" },
        new Music { Title = "Unknown Pleasures", Author = "Unknown Mortal Orchestra", Album = "Album" }), "legitimate titles and artists beginning with Unknown remain eligible");
    Check(!SongVersionPolicy.Matches(new Music { Title = "Song", Author = "Unknown Artist", Album = "Album" },
        new Music { Title = "Song", Author = "Unknown Artist", Album = "Album" }), "unknown placeholder tags do not produce candidates");
    await Conflict(() => store.SetPreferredAsync(flac.Id, mp3.Id), "preference cannot pin a version from another song");
    var firstLyrics = await lyrics.GetAsync(flac.Id);
    var secondLyrics = await lyrics.GetAsync(mp3.Id);
    Check(await lyrics.SaveAsync(flac.Id, new(new("[00:01.00]A", LyricsFormat.Lrc), null), firstLyrics.Revision, "User", expectedOwnerId: firstLyrics.OwnerId), "save initial shared lyrics");
    Check(await lyrics.SaveAsync(mp3.Id, new(new("[00:02.00]B", LyricsFormat.Lrc), null), secondLyrics.Revision, "User", expectedOwnerId: secondLyrics.OwnerId), "save independent original lyrics");
    var preview = await store.PreviewMergeAsync(flac.Id, mp3.Id, lyrics);
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { await store.MergeAsync(preview, preview.TargetSongId, false, canceled.Token); }
        catch (OperationCanceledException) { Check(await database.Table<VersionLyricsOverrideRecord>().CountAsync() == 0, "canceled merge commits no overrides or partial state"); }
    }
    await store.MergeAsync(preview, preview.TargetSongId, false);
    var grouped = await store.GetVersionsAsync(flac.Id);
    Check(grouped.Count == 2 && grouped[0].SongId == grouped[1].SongId, "merge changes group membership only");
    Check(grouped[0].Genre == "Pop" && grouped[1].Genre == "Rock" && grouped[0].Year != grouped[1].Year, "per-version info retained");
    Check(grouped[0].IsFavorite && grouped[1].IsFavorite, "favorite OR is shared");
    await database.ExecuteAsync("UPDATE Music SET IsMissing=1 WHERE Id=?", flac.Id);
    Check((await store.GetSongAsync(flac.Id))!.IsFavorite && (await lyrics.GetAsync(flac.Id)).Document.Original.Content.Contains('A'), "missing file retains song favorite and lyrics");
    await database.ExecuteAsync("UPDATE Music SET IsMissing=0 WHERE Id=?", flac.Id);
    Check(await database.Table<PlayListMusic>().CountAsync() == 2, "duplicate playlist positions retained");
    Check((await lyrics.GetAsync(mp3.Id)).Document.Original.Content.Contains('B'), "different timeline retained as version override");
    Check((await lyrics.GetSharedAsync(mp3.Id)).Document.Original.Content.Contains('A'), "shared lyrics chosen explicitly");
    var operation = await store.GetLastOperationAsync(preview.TargetSongId);
    await store.UndoAsync(operation!.Id);
    Check((await database.FindAsync<Music>(flac.Id)).SongId != (await database.FindAsync<Music>(mp3.Id)).SongId, "undo restores original groups");
    Check(await database.FindAsync<SongRecord>(preview.TargetSongId) is not null && await database.FindAsync<SongRecord>(preview.OtherSongId) is not null,
        "undo preserves original auto-increment identities");
    Check(await database.FindAsync<PlayListMusic>(1) is not null && await database.FindAsync<PlayListMusic>(2) is not null, "undo preserves playlist entry ids");
    Check(await database.Table<PlayListMusic>().CountAsync() == 2, "undo retains playlist identities");
    var stale = await store.PreviewMergeAsync(flac.Id, mp3.Id, lyrics);
    await store.SetPreferredAsync(flac.Id, flac.Id);
    await Conflict(() => store.MergeAsync(stale, stale.TargetSongId, false), "stale preview rejected");
    Check((await database.FindAsync<Music>(flac.Id)).SongId != (await database.FindAsync<Music>(mp3.Id)).SongId, "rejected merge publishes no partial group");
    preview = await store.PreviewMergeAsync(flac.Id, mp3.Id, lyrics);
    await store.MergeAsync(preview, preview.TargetSongId, false);
    var oldOwner = await lyrics.GetAsync(mp3.Id);
    await store.UseSharedLyricsAsync(mp3.Id);
    Check(!await lyrics.SaveAsync(mp3.Id, oldOwner.Document, oldOwner.Revision, "User", expectedOwnerId: oldOwner.OwnerId), "late override editor cannot overwrite shared lyrics");
    await store.SplitAsync(mp3.Id, lyrics);
    Check((await database.FindAsync<Music>(flac.Id)).SongId != (await database.FindAsync<Music>(mp3.Id)).SongId, "split restores independent ownership");
    var splitOperation = await store.GetLastOperationAsync(preview.TargetSongId);
    await store.UndoAsync(splitOperation!.Id);
    Check((await store.GetVersionsAsync(flac.Id)).Count == 2, "split can be undone without losing versions");
    var current = await lyrics.GetSharedAsync(flac.Id);
    await lyrics.SaveAsync(flac.Id, new(new("[00:03.00]Edited", LyricsFormat.Lrc), null), current.Revision, "User", expectedOwnerId: current.OwnerId);
    var mergeOperation = await store.GetLastOperationAsync(preview.TargetSongId);
    await Conflict(() => store.UndoAsync(mergeOperation!.Id), "recovery refuses to overwrite later edits");
    Check((await lyrics.GetSharedAsync(flac.Id)).Document.Original.Content.Contains("Edited"), "later edits preserved");
    await database.RunInTransactionAsync(db => LyricsRepository.SaveInTransaction(db, flac.Id, current.Document, -1, "User", false));
    Check((await lyrics.GetSharedAsync(flac.Id)).Revision == current.Revision + 1, "metadata-only save does not advance shared lyrics");
    await Conflict(() => store.RemoveMissingVersionAsync(mp3.Id), "a restored or available file cannot be removed as missing");
    await database.ExecuteAsync("UPDATE Music SET IsMissing=1 WHERE Id=?", flac.Id);
    await store.SetPreferredAsync(flac.Id, flac.Id);
    await store.RemoveMissingVersionAsync(flac.Id);
    var remaining = (await store.GetVersionsAsync(flac.Id)).Single();
    Check(remaining.Id == mp3.Id && remaining.SongPreferredMusicId is null && remaining.IsFavorite, "removing a missing preferred version keeps the song and restores automatic selection");
    Check((await lyrics.GetSharedAsync(mp3.Id)).Document.Original.Content.Contains("Edited") &&
        await database.Table<PlayListMusic>().CountAsync() == 2, "removal keeps shared lyrics and both playlist occurrences");
    await Conflict(() => store.RemoveMissingVersionAsync(flac.Id), "a stale second removal is rejected");
    await database.ExecuteAsync("UPDATE Music SET IsMissing=1 WHERE Id=?", mp3.Id);
    await store.RemoveMissingVersionAsync(mp3.Id);
    Check((await store.GetVersionsAsync(mp3.Id)).Count == 0 && (await store.GetSongAsync(mp3.Id))!.IsFavorite, "removal of the last version preserves the recoverable song identity");
    Console.WriteLine($"{checks} checks passed");
}
finally
{
    await database.CloseAsync();
    string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
    string resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture path");
    Directory.Delete(resolved, true);
}
