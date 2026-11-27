using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService(SQLiteAsyncConnection db)
{
    private readonly SQLiteAsyncConnection _dbConnection = db;

    // The song ownership insert is real. Automatic cross-version matching has its own regression project.
    private static Task<int> ApplyAutomaticSongLinksAsync(IReadOnlyList<Music> batch) => Task.FromResult(0);
}
