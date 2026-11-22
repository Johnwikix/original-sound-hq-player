using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using SQLite;

namespace WinUIMusicPlayer.Services;

internal static class CueLibrarySchema
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CueSheetRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CueSourceRecord))]
    public static Task InitializeAsync(SQLiteAsyncConnection connection) => connection.RunInTransactionAsync(db =>
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS CueSheet (
                Id INTEGER PRIMARY KEY, Path TEXT NOT NULL, PathKey TEXT NOT NULL UNIQUE,
                DirectoryKey TEXT NOT NULL, Fingerprint TEXT NOT NULL, IsActive INTEGER NOT NULL)
            """);
        db.Execute("CREATE INDEX IF NOT EXISTS IX_CueSheet_Directory ON CueSheet(DirectoryKey)");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS CueSource (
                CueSheetId INTEGER NOT NULL, Path TEXT NOT NULL COLLATE NOCASE,
                Length INTEGER NOT NULL, LastWriteUtcTicks INTEGER NOT NULL,
                SampleRate INTEGER NOT NULL, TotalSamples INTEGER NOT NULL,
                PRIMARY KEY (CueSheetId,Path)) WITHOUT ROWID
            """);
        db.Execute("CREATE INDEX IF NOT EXISTS IX_CueSource_Path ON CueSource(Path)");
        db.Execute("CREATE INDEX IF NOT EXISTS IX_Music_LocalFolder ON Music(FolderPath COLLATE NOCASE) WHERE SourceId=0");
        db.Execute("CREATE INDEX IF NOT EXISTS IX_Music_CueSheet ON Music(CueSheetId)");
        db.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Music_CurrentCueTrack ON Music(CueSheetId,CueTrackNo) WHERE CueMappingActive=1");
    });
}

internal sealed class CueSheetRecord
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public bool IsActive { get; set; }
}

internal sealed class CueSourceRecord
{
    public int CueSheetId { get; set; }
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public int SampleRate { get; set; }
    public long TotalSamples { get; set; }
}
