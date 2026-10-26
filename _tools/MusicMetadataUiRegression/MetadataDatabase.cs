using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace WinUIMusicPlayer.Services;

// Only supply the service boundary; migration, file reads and backfill use production code.
public partial class MusicDatabaseService(SQLiteAsyncConnection connection)
{
    private readonly SQLiteAsyncConnection _dbConnection = connection;
    private readonly ILogger<MusicDatabaseService> _logger = NullLogger<MusicDatabaseService>.Instance;
    public Task WhenInitialized => Task.CompletedTask;
}
