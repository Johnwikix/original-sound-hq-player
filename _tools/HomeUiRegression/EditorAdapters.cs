using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

internal sealed class FixtureEditorFactory(MusicDatabaseService database, AppViewModel app) : IMusicDetailsEditorFactory, IMusicDetailsEditorHost
{
    internal EditorSessions Sessions { get; } = new();
    internal ApplicationTasks Tasks { get; } = new(app.State.Lifecycle);
    internal LyricsOnlineSearch Online { get; } = new();
    internal Task CoverGate { get; set; } = Task.CompletedTask;
    internal int CoverReads;
    public MusicDetailsEditorViewModel Create(Music music)
        => new(music, database, new LyricsEditorViewModel(music, database, new(), Online, Tasks), Sessions, this,
            NullLogger<MusicDetailsEditorViewModel>.Instance);
    public async Task<byte[]?> ReadCoverAsync(Music music, CancellationToken token)
    {
        CoverReads++;
        await CoverGate; // Keep a real uncancellable boundary for close/drain regression.
        return await ToolUtils.GetRawImage(music, true, token);
    }
    public Task<byte[]?> FindCoverAsync(Music music, CancellationToken token) => ToolUtils.GetRawImage(music, true, token);
    public Task<byte[]?> ChooseCoverAsync(CancellationToken token) => Task.FromResult<byte[]?>(null);
    public Task ExportCoverAsync(byte[] cover, CancellationToken token) => File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "exported-cover.bmp"), cover, token);
    public void OpenLocation(Music music) { }
    public void MetadataChanged() => app.NotifySongsSourceChanged();
    public void NotifyWriteQueued(Music music) { }
}
namespace WinUIMusicPlayer.Services
{
    public sealed record FixtureLyricsCache(LyricsDocument Document, long Revision);
    public static class OneShotLyricsCache
    {
        public static FixtureLyricsCache? Load(string path) => null;
        public static bool SaveEdited(string path, LyricsDocument document, long revision) => true;
    }
    public partial class MusicDatabaseService
    {
        public Task EditorSaveGate { get; set; } = Task.CompletedTask;
        public bool FailEditorSave { get; set; }
        public int EditorSavesStarted;
        public async Task<AdditionalMusicMetadata?> ReadAdditionalMetadataAsync(Music music, CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return AdditionalMusicMetadata.From(music);
        }
        public Task SaveDetailsAsync(Music music, LyricsDocument document, long revision, CancellationToken token, bool lyricsChanged = true)
            => SaveEditorAsync(music, null, document, revision, token, lyricsChanged, false);
        public Task QueueMetadataWriteAsync(Music music, byte[]? cover, LyricsDocument document, long revision, CancellationToken token, bool lyricsChanged = true)
            => SaveEditorAsync(music, cover, document, revision, token, lyricsChanged, true);
        private async Task SaveEditorAsync(Music music, byte[]? cover, LyricsDocument document, long revision, CancellationToken token, bool lyricsChanged, bool queue)
        {
            EditorSavesStarted++;
            await EditorSaveGate;
            if (FailEditorSave) throw new IOException("fixture metadata save failure");
            await _dbConnection.RunInTransactionAsync(connection =>
            {
                token.ThrowIfCancellationRequested();
                LyricsRepository.SaveInTransaction(connection, music.Id, document, revision, "User", lyricsChanged);
                connection.Execute("UPDATE Music SET Title=?,Author=?,Album=?,Year=?,TrackNumber=?,DiskNumber=?,UpdateTime=? WHERE Id=?",
                    music.Title, music.Author, music.Album, music.Year, music.TrackNumber, music.DiskNumber, music.UpdateTime, music.Id);
                if (music.AdditionalMetadataRead) MusicMetadataStorage.Save(connection, music.Id, AdditionalMusicMetadata.From(music));
                if (queue) connection.InsertOrReplace(new PendingMetadataWrite { Path = music.Path, Title = music.Title, Author = music.Author,
                    Album = music.Album, Year = music.Year, TrackNumber = music.TrackNumber, DiskNumber = music.DiskNumber,
                    Cover = cover is null ? null : (byte[])cover.Clone(), Lyrics = document.Original.Content,
                    LyricsFormat = document.Original.Format, TranslationLrc = document.TranslationLrc, PronunciationLrc = document.PronunciationLrc,
                    LyricsSchemaVersion = 2, MetadataVersion = 1, Genre = music.Genre, AlbumArtist = music.AlbumArtist,
                    Composer = music.Composer, Lyricist = music.Lyricist, Conductor = music.Conductor, CatalogNumber = music.CatalogNumber });
            });
        }
    }
}
namespace WinUIMusicPlayer.Services.Lyrics
{
    public sealed class LyricsOnlineSearch
    {
        public int Calls;
        public Func<Music, bool, CancellationToken, Task<LyricsDocument?>> Handler { get; set; } = (_, _, _) => Task.FromResult<LyricsDocument?>(null);
        public Task<LyricsDocument?> SearchAsync(Music music, bool force, CancellationToken token)
        {
            Calls++;
            return Handler(music, force, token);
        }
    }
    public interface ILlmTranslationService
    {
        Task<string?> TranslateManuallyAsync(Music music, LyricsDocument document, CancellationToken token);
    }
}

namespace WinUIMusicPlayer.Services
{
    /// <summary>提供真实编辑器工厂需要的通知边界，不访问系统通知或曲库投影。</summary>
    public sealed class LibraryProjectionService
    {
        /// <summary>隔离全库投影刷新。</summary>
        public void RequestRefresh() { }
    }
    /// <summary>隔离本回归不触发的系统通知。</summary>
    public sealed class NotificationService
    {
        /// <summary>隔离系统通知。</summary>
        public void SendNotification(string title, string message) { }
    }
}
namespace WinUIMusicPlayer.WebService
{
    /// <summary>为实际 EditorHost 提供可控异步封面来源，保留取消与收尾时序。</summary>
    public sealed class LrcService
    {
        internal Func<CancellationToken, Task<byte[]?>> Handler { get; set; } = _ => Task.FromResult<byte[]?>(null);
        /// <summary>把实际工厂传入的令牌交给异步测试来源。</summary>
        public Task<byte[]?> GetMixedCoverImageAsync(Music music, CancellationToken token = default) => Handler(token);
    }
}
