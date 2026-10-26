using System.Buffers.Binary;
using System.Text;
using ATL;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

string root = Path.Combine(Path.GetTempPath(), "music-metadata-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var source = new Music { Id = 1, Path = "test.flac", Title = "original", Author = "artist", PlayCount = 7, IsFavorite = true };
    var editor = new MusicMetadataEditorViewModel(source);
    Check(!editor.IsAdditionalExpanded && AdditionalMusicMetadata.From(editor.Draft).IsEmpty, "Missing metadata is empty and collapsed, with no invented labels");
    Check(!editor.Capture().AdditionalMetadataRead, "Unknown tags are not mistaken for an explicit clear");
    editor.Draft.Title = "draft";
    Check(source.Title == "original", "Typing does not mutate the library row");
    editor.Draft.Genre = "Jazz";
    var snapshot = editor.Capture();
    Check(snapshot.AdditionalMetadataRead, "Explicit optional edits create a known tag snapshot");
    editor.Draft.Title = "newer draft";
    Check(!editor.AcceptSaved(snapshot) && editor.Draft.Title == "newer draft" && source.Title == "draft", "Late edits remain in the window while the committed snapshot is published");
    Check(source.PlayCount == 7 && source.IsFavorite, "Metadata commit preserves playback-owned state");
    var emptyEditor = new MusicMetadataEditorViewModel(new Music { AdditionalMetadataRead = true });
    Check(emptyEditor.Capture().AdditionalMetadataRead && !emptyEditor.IsAdditionalExpanded, "Known-empty tags remain distinct from unread tags");
    Check(!emptyEditor.ApplyLoaded(AdditionalMusicMetadata.From(new Music())), "Confirming absent tags does not request a library list rebuild");
    var loadingEditor = new MusicMetadataEditorViewModel(new Music());
    var tags = new AdditionalMusicMetadata("专辑艺术家", "Classical", "作曲者", "作词者", "指挥", "CAT-001");
    loadingEditor.ApplyLoaded(tags);
    Check(loadingEditor.IsAdditionalExpanded && AdditionalMusicMetadata.From(loadingEditor.Draft) == tags, "On-demand file metadata populates the draft");
    Check(tags.Matches("cat-001") && !default(AdditionalMusicMetadata).Matches("anything"), "Sparse metadata search handles default nulls and case folding");

    var db = new SQLiteAsyncConnection(Path.Combine(root, "old.db"));
    await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,Path TEXT,Title TEXT,PlayCount INTEGER)");
    await db.ExecuteAsync("CREATE TABLE PendingMetadataWrite(Path TEXT PRIMARY KEY,Title TEXT)");
    await db.ExecuteAsync("INSERT INTO Music VALUES(1,'test.flac','old title',9)");
    await db.ExecuteAsync("INSERT INTO PendingMetadataWrite VALUES('legacy.flac','legacy title')");
    await MusicMetadataStorage.EnsureSchemaAsync(db);
    int columns = (await db.GetTableInfoAsync("Music")).Count;
    await MusicMetadataStorage.EnsureSchemaAsync(db);
    Check((await db.GetTableInfoAsync("Music")).Count == columns, "Old database migration is repeatable");
    var row = await db.FindAsync<Music>(1);
    var legacy = await db.FindAsync<PendingMetadataWrite>("legacy.flac");
    Check(!row.AdditionalMetadataRead && row.Genre == "" && legacy.MetadataVersion == 0, "Migration preserves unread and legacy-queue semantics");
    Check(await MusicMetadataStorage.SaveIfUnreadAsync(db, 1, "test.flac", tags) == 1, "One-track metadata backfill commits once");
    Check(await MusicMetadataStorage.SaveIfUnreadAsync(db, 1, "test.flac", default) == 0, "Late backfill cannot overwrite known metadata");
    row = await db.FindAsync<Music>(1);
    Check(row.Title == "old title" && row.PlayCount == 9 && AdditionalMusicMetadata.From(row) == tags, "Backfill changes only optional metadata columns");
    await db.ExecuteAsync("UPDATE Music SET AdditionalMetadataRead=0 WHERE Id=1");
    await db.ExecuteAsync("INSERT INTO PendingMetadataWrite(Path,Title) VALUES('test.flac','pending')");
    Check(await MusicMetadataStorage.SaveIfUnreadAsync(db, 1, "test.flac", default) == 0, "Pending file edits block stale tag backfill");
    await db.RunInTransactionAsync(connection => MusicMetadataStorage.Save(connection, 1, default));
    row = await db.FindAsync<Music>(1);
    Check(row.AdditionalMetadataRead && row.AlbumArtist == "" && row.Genre == "", "Clearing metadata stores empty strings even for a default value snapshot");
    await db.CloseAsync();

    foreach (string extension in new[] { ".wav", ".mp3", ".flac", ".wma", ".aiff" })
    {
        string path = Path.Combine(root, "generated" + extension);
        if (extension == ".wav")
        {
            var bytes = new byte[44 + 8820];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 88200);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
            Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 8820);
            await File.WriteAllBytesAsync(path, bytes);
        }
        else if (extension == ".mp3")
        {
            var bytes = new byte[417 * 8];
            for (int i = 0; i < 8; i++) new byte[] { 255, 251, 144, 192 }.CopyTo(bytes, i * 417);
            await File.WriteAllBytesAsync(path, bytes);
        }
        else if (extension == ".wma") await File.WriteAllBytesAsync(path, Convert.FromBase64String("MCaydY5mzxGm2QCqAGLObOYAAAAAAAAAAgAAAAECodyrjEepzxGO5ADADCBTZWgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARgDAAAAAAAAAAAAAAAAAAABAAAAAAAAAEBCDwAAAAAAQEIPAAAAAAAAAAAAAAAAAAIAAAAAAgAAAAIAAEDECgCRB9y3t6nPEY7mAMAMIFNlYAAAAAAAAABAnmn4TVvPEaj9AIBfXEQrAFf7IFVbzxGo/QCAX1xEKwAAAAAAAAAAEgAAAAAAAAABAAAAAAABAAEARKwAAIhYAQACABAAAAA2JrJ1jmbPEabZAKoAYs5sMgIAAAAAAAAAAAAAAAAAAAAAAAAAAAABAQAAAAAAAAABAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        else if (extension == ".aiff") await File.WriteAllBytesAsync(path, Convert.FromBase64String("Rk9STQAAAi5BSUZGQ09NTQAAABIAAQAAAQAAEEAOrEQAAAAAAABTU05EAAACCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
        else await File.WriteAllBytesAsync(path, Convert.FromBase64String("ZkxhQ4AAACIBAAEAAAAAAAAACsRA8AAAAQC/YZ6sDN8/aNSW6pNEE36L//hgCAD/SAAAAJwi"));

        var track = new Track(path);
        Check(AudioMetadataTags.Read(track).IsEmpty, extension + " missing optional fields stay empty");
        AudioMetadataTags.Write(track, tags);
        Check(track.Save(), extension + " save succeeds");
        Check(AudioMetadataTags.Read(new Track(path)) == tags, extension + " all six unified properties survive reopen");
        var titleOnly = new Track(path) { Title = "title only" };
        Check(titleOnly.Save() && AudioMetadataTags.Read(new Track(path)) == tags, extension + " title-only edits preserve existing optional tags");
        track = new Track(path);
        AudioMetadataTags.Write(track, default);
        Check(track.Save() && AudioMetadataTags.Read(new Track(path)).IsEmpty, extension + " explicit optional clear survives reopen");
    }
    Console.WriteLine($"ALL {checks} METADATA CHECKS PASSED");
}
finally
{
    SQLiteAsyncConnection.ResetPool();
    Directory.Delete(root, recursive: true);
}
