using System;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>迁移并持久化可选标签；不触发媒体扫描，也不覆盖播放统计。</summary>
public static class MusicMetadataStorage
{
    /// <summary>判断事务连接是否已经启用歌曲分组。</summary>
    public static bool HasSongs(SQLiteConnection db) => db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Song'") != 0;
    /// <summary>为扫描新文件建立身份并排入自动关联；旧库未启用分组时保持兼容。</summary>
    public static void AttachSong(SQLiteConnection db, Music music)
    {
        if (music.SongId > 0 || !HasSongs(db)) return;
        db.Execute("INSERT INTO Song(Title,Artist,Album,IsFavorite,FavoriteOrder,Revision,LyricsSourceMusicId) VALUES(?,?,?,?,?,1,?)",
            music.Title, music.Author, music.Album, music.IsFavorite, music.Order, music.Id);
        music.SongId = db.ExecuteScalar<int>("SELECT last_insert_rowid()");
        db.Execute("UPDATE Music SET SongId=? WHERE Id=?", music.SongId, music.Id);
    }

    /// <summary>单版本歌曲跟随标签变更更新关联身份；多版本组的文件编辑不隐式拆组。</summary>
    public static void UpdateSongIdentity(SQLiteConnection db, int musicId, string title, string author, string album)
    {
        if (!HasSongs(db)) return;
        db.Execute("UPDATE Song SET Title=?,Artist=?,Album=?,Revision=Revision+1 WHERE Id=(SELECT SongId FROM Music WHERE Id=?) " +
            "AND (SELECT COUNT(*) FROM Music WHERE SongId=Song.Id AND IsDetached=0)=1 AND (Title<>? OR Artist<>? OR Album<>?)",
            title, author, album, musicId, title, author, album);
    }
    private static readonly string[] Fields = ["AlbumArtist", "Genre", "Composer", "Lyricist", "Conductor", "CatalogNumber"];
    private const string UpdateSql = "UPDATE Music SET AlbumArtist=?,Genre=?,Composer=?,Lyricist=?,Conductor=?,CatalogNumber=?,AdditionalMetadataRead=1 WHERE Id=?";

    /// <summary>幂等补齐曲目与旧待写队列的标签列。</summary>
    public static async Task EnsureSchemaAsync(SQLiteAsyncConnection db)
    {
        await EnsureTableAsync(db, "Music", "AdditionalMetadataRead");
        await EnsureTableAsync(db, "PendingMetadataWrite", "MetadataVersion");
    }

    private static async Task EnsureTableAsync(SQLiteAsyncConnection db, string table, string marker)
    {
        var columns = await db.GetTableInfoAsync(table);
        foreach (string name in Fields)
            if (!columns.Exists(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                await db.ExecuteAsync("ALTER TABLE [" + table + "] ADD COLUMN [" + name + "] TEXT NOT NULL DEFAULT ''");
        if (!columns.Exists(column => column.Name.Equals(marker, StringComparison.OrdinalIgnoreCase)))
            await db.ExecuteAsync("ALTER TABLE [" + table + "] ADD COLUMN [" + marker + "] INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>在现有编辑或扫描事务内更新已读取的标签。</summary>
    public static int Save(SQLiteConnection db, int id, AdditionalMusicMetadata metadata) =>
        db.Execute(UpdateSql, metadata.AlbumArtist ?? "", metadata.Genre ?? "", metadata.Composer ?? "", metadata.Lyricist ?? "", metadata.Conductor ?? "", metadata.CatalogNumber ?? "", id);

    /// <summary>只补齐未读取且没有待写编辑的记录，防止迟到的文件读取覆盖编辑。</summary>
    public static Task<int> SaveIfUnreadAsync(SQLiteAsyncConnection db, int id, string path, AdditionalMusicMetadata metadata) =>
        db.ExecuteAsync(UpdateSql + " AND Path=? COLLATE NOCASE AND coalesce(AdditionalMetadataRead,0)=0 " +
            "AND NOT EXISTS(SELECT 1 FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE)",
            metadata.AlbumArtist ?? "", metadata.Genre ?? "", metadata.Composer ?? "", metadata.Lyricist ?? "", metadata.Conductor ?? "", metadata.CatalogNumber ?? "", id, path, path);
}
