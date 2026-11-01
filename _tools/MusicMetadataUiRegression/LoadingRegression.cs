using System.Buffers.Binary;
using ATL;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace MusicMetadataUiRegression;

internal static class LoadingRegression
{
    public static async Task RunAsync(Window window)
    {
        string root = Path.Combine(Path.GetTempPath(), "metadata-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new SQLiteAsyncConnection(Path.Combine(root, "old.db"));
        var tasks = new ApplicationTasks(new AppLifecycle());
        try
        {
            // Start with a pre-optional-tags database, without running a library rescan.
            await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,Path TEXT,Title TEXT)");
            await db.ExecuteAsync("CREATE TABLE PendingMetadataWrite(Path TEXT PRIMARY KEY)");
            await MusicMetadataStorage.EnsureSchemaAsync(db);
            string path = Path.Combine(root, "tagged.wav");
            byte[] wave = new byte[44 + 8820];
            "RIFF"u8.CopyTo(wave);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 44100);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 88200);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
            "data"u8.CopyTo(wave.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), 8820);
            await File.WriteAllBytesAsync(path, wave);
            if (!new Track(path) { Genre = "Jazz" }.Save()) throw new Exception("Cannot create tagged fixture");
            await db.ExecuteAsync("INSERT INTO Music(Id,Path,Title) VALUES(1,?,'Old library song')", path);
            var music = await db.FindAsync<Music>(1);
            if (music.AdditionalMetadataRead) throw new Exception("Fixture must need backfill");
            var panel = new MetadataPanel(music, isLoading: true);
            int loadingChanges = 0;
            panel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(panel.IsLoading)) return;
                if (!panel.DispatcherQueue.HasThreadAccess) throw new Exception("Loading notification left the UI thread");
                loadingChanges++;
            };
            window.Content = panel;
            window.AppWindow.MoveAndResize(new(-30000, -30000, 850, 700));
            window.Activate();
            await Task.Delay(150);
            CheckState(panel, true, "initial loading");
            var service = new MusicDatabaseService(db);
            await tasks.RunAsync(async token =>
            {
                try
                {
                    var tags = await service.ReadAdditionalMetadataAsync(music, token);
                    if (tags is not { Genre: "Jazz" }) throw new Exception("Real file backfill did not complete");
                    panel.MetadataEditor.ApplyLoaded(tags.Value);
                }
                finally { panel.IsLoading = false; }
            }).WaitAsync(TimeSpan.FromSeconds(10));
            if (!(await db.FindAsync<Music>(1)).AdditionalMetadataRead || panel.MetadataEditor.Draft.Genre != "Jazz")
                throw new Exception("Backfill must persist and populate the draft before checking UI");
            if (loadingChanges != 1) throw new Exception("IsLoading notification must be published once after backfill");
            await Task.Delay(50);
            CheckState(panel, false, "old-library tag backfill finished (IsLoading=false)");
            // Exercise the saving finally path, including an exception followed by a retry.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    panel.IsLoading = true;
                    CheckState(panel, true, "saving");
                    await Task.Delay(20);
                    if (attempt == 0) throw new IOException("Simulated save failure");
                }
                catch (IOException) when (attempt == 0) { }
                finally { panel.IsLoading = false; }
                CheckState(panel, false, "save failure/retry completion");
            }
        }
        finally
        {
            await tasks.DrainAsync();
            window.Content = null;
            await db.CloseAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckState(MetadataPanel panel, bool loading, string stage)
    {
        var overlay = (Grid)panel.FindName("LoadingOverlay");
        var editor = (StackPanel)panel.FindName("EditorContent");
        if (overlay.Visibility != (loading ? Visibility.Visible : Visibility.Collapsed) ||
            editor.Visibility != (loading ? Visibility.Collapsed : Visibility.Visible))
            throw new Exception($"{stage}: overlay={overlay.Visibility}, editor={editor.Visibility}; expected loading={loading}");
    }
}
