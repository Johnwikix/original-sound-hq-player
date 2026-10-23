using SQLite;
using WinUIMusicPlayer.Services.LastFm;

internal static class StoreChecks
{
    internal static string ReadState(LastFmStore store)
    {
        using var connection = new SQLiteConnection(store.DatabasePath, SQLiteOpenFlags.ReadOnly);
        return connection.ExecuteScalar<string>("SELECT Json FROM LastFmState WHERE Id=1");
    }
}

// A separate real SQLite connection makes the production transaction fail while reads remain available.
internal sealed class StoreWriteBlocker : IDisposable
{
    private readonly SQLiteConnection _connection;
    internal StoreWriteBlocker(LastFmStore store)
    {
        _connection = new SQLiteConnection(store.DatabasePath);
        _connection.Execute("BEGIN IMMEDIATE");
    }
    public void Dispose()
    {
        _connection.Execute("ROLLBACK");
        _connection.Dispose();
    }
}
