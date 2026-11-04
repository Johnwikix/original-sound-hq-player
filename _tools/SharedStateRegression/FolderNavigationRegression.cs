using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

/// <summary>通过生产目录 ViewModel 和投影服务验证目录切换与缓存的一致性。</summary>
internal static class FolderNavigationRegression
{
    public static async Task RunAsync()
    {
        var app = new AppViewModel();
        app.State.Browse.SelectedSortOption = new SortOption { Tag = "DefaultOrder" };
        app.State.Library.Replace([
            new Music(1, @"D:\Music\A\one.flac") { FolderPath = @"D:\Music\A" },
            new Music(2, @"D:\Music\B\two.flac") { FolderPath = @"D:\Music\B" }
        ]);
        var database = new MusicDatabaseService { Roots = [new() { Path = @"D:\Music", Name = "Music" }] };
        var projections = new LibraryProjectionService(app.State, new LibraryQueries(app.State.Library),
            NullLogger<LibraryProjectionService>.Instance);
        var viewModel = new FolderViewModel(new(), app, database, projections, NullLogger<FolderViewModel>.Instance);
        bool notificationsOnUiThread = true;
        app.FolderSongs.CollectionChanged += (_, _) => notificationsOnUiThread &= WinUIMusicPlayer.App.MainWindow.DispatcherQueue.HasThreadAccess;
        viewModel.PropertyChanged += (_, _) => notificationsOnUiThread &= WinUIMusicPlayer.App.MainWindow.DispatcherQueue.HasThreadAccess;
        try
        {
            await viewModel.ActivateAsync();
            await WaitForIdleAsync(viewModel);
            var root = viewModel.Roots.Single();
            var a = root.Children.Single(node => node.Name == "A");
            var b = root.Children.Single(node => node.Name == "B");
            viewModel.SelectFolderCommand.Execute(a);
            await WaitForIdleAsync(viewModel);
            Check(viewModel.HasSongs && app.FolderSongs.Single().Id == 1, "Initial folder A must publish its song.");
            int initialFills = app.FolderSongs.FillCount;
            await projections.UpdateSongCollectionsAsync(app.FolderSongs, SongViewType.Folder, a.Location.Matches);
            Check(app.FolderSongs.FillCount == initialFills, "An unchanged folder must reuse its published projection.");

            viewModel.SelectFolderCommand.Execute(b);
            viewModel.SelectFolderCommand.Execute(a);
            Check(!viewModel.HasSongs && app.FolderSongs.Count == 0, "Pending folder changes must hide and clear old playable songs.");
            await WaitForIdleAsync(viewModel);
            Check(viewModel.SelectedNode == a && viewModel.HasSongs && app.FolderSongs.Count == 1 && app.FolderSongs[0].Id == 1,
                "A -> B -> A before publication must restore A's cached song instead of leaving an empty collection.");

            // 暂占真实服务的共享计算门，模拟另一个列表排序尚未结束，确定性覆盖跨 UI 调度轮次。
            var gate = (SemaphoreSlim)typeof(LibraryProjectionService).GetField("_computeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(projections)!;
            await gate.WaitAsync();
            try
            {
                viewModel.SelectFolderCommand.Execute(b);
                await Task.Yield();
                Check(viewModel.IsBusy && app.FolderSongs.Count == 0, "Folder B must wait behind the active projection without exposing A's songs.");
                viewModel.SelectFolderCommand.Execute(a);
            }
            finally { gate.Release(); }
            await WaitForIdleAsync(viewModel);
            Check(viewModel.SelectedNode == a && viewModel.HasSongs && app.FolderSongs.Count == 1 && app.FolderSongs[0].Id == 1,
                "Returning to A across UI turns must republish its song after the shared compute gate opens.");

            int restoredFills = app.FolderSongs.FillCount;
            await projections.UpdateSongCollectionsAsync(app.FolderSongs, SongViewType.Folder, a.Location.Matches);
            Check(app.FolderSongs.FillCount == restoredFills, "The restored folder must become cacheable again.");
            viewModel.SelectFolderCommand.Execute(b);
            await WaitForIdleAsync(viewModel);
            Check(viewModel.HasSongs && app.FolderSongs.Single().Id == 2, "Ordinary navigation must still publish folder B.");
            Check(notificationsOnUiThread, "Folder collections and availability notifications must stay on the UI context.");
        }
        finally
        {
            await viewModel.StopAsync();
            await projections.StopAsync();
        }
        Console.WriteLine("PASS: production folder navigation, rapid round trips, shared compute wait, cache reuse and UI notifications.");
    }

    private static async Task WaitForIdleAsync(FolderViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (viewModel.IsBusy)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Folder navigation did not finish.");
            await Task.Delay(1);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
