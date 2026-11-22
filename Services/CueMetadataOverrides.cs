using SQLite;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>Null inherits the scanned value. Empty strings and zero are explicit user edits.</summary>
[Table("CueMetadataOverride")]
internal sealed class CueMetadataOverrides
{
    [PrimaryKey] public int MusicId { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Album { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiskNumber { get; set; }
    public int? Year { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public string? Composer { get; set; }
    public string? Lyricist { get; set; }
    public string? Conductor { get; set; }
    public string? CatalogNumber { get; set; }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CueMetadataOverrides))]
    internal static Task EnsureSchemaAsync(SQLiteAsyncConnection database) => database.CreateTableAsync<CueMetadataOverrides>();

    internal static void Capture(SQLiteConnection db, Music before, Music after)
    {
        var row = db.Find<CueMetadataOverrides>(before.Id) ?? new() { MusicId = before.Id };
        if (before.Title != after.Title) row.Title = after.Title;
        if (before.Author != after.Author) row.Author = after.Author;
        if (before.Album != after.Album) row.Album = after.Album;
        if (before.TrackNumber != after.TrackNumber) row.TrackNumber = after.TrackNumber;
        if (before.DiskNumber != after.DiskNumber) row.DiskNumber = after.DiskNumber;
        if (before.Year != after.Year) row.Year = after.Year;
        if (before.AlbumArtist != after.AlbumArtist) row.AlbumArtist = after.AlbumArtist;
        if (before.Genre != after.Genre) row.Genre = after.Genre;
        if (before.Composer != after.Composer) row.Composer = after.Composer;
        if (before.Lyricist != after.Lyricist) row.Lyricist = after.Lyricist;
        if (before.Conductor != after.Conductor) row.Conductor = after.Conductor;
        if (before.CatalogNumber != after.CatalogNumber) row.CatalogNumber = after.CatalogNumber;
        db.InsertOrReplace(row);
    }

    internal static void Apply(SQLiteConnection db, Music incoming)
    {
        if (db.Find<CueMetadataOverrides>(incoming.Id) is not { } row) return;
        incoming.Title = row.Title ?? incoming.Title;
        incoming.Author = row.Author ?? incoming.Author;
        incoming.Album = row.Album ?? incoming.Album;
        incoming.TrackNumber = row.TrackNumber ?? incoming.TrackNumber;
        incoming.DiskNumber = row.DiskNumber ?? incoming.DiskNumber;
        incoming.Year = row.Year ?? incoming.Year;
        incoming.AlbumArtist = row.AlbumArtist ?? incoming.AlbumArtist;
        incoming.Genre = row.Genre ?? incoming.Genre;
        incoming.Composer = row.Composer ?? incoming.Composer;
        incoming.Lyricist = row.Lyricist ?? incoming.Lyricist;
        incoming.Conductor = row.Conductor ?? incoming.Conductor;
        incoming.CatalogNumber = row.CatalogNumber ?? incoming.CatalogNumber;
        if (row.Title is not null) incoming.TitleFromFileName = false;
    }
}
