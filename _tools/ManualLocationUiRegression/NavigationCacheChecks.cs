using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.View;

internal sealed partial class TestWindow
{
    private readonly List<WeakReference<Page>> _cachePages = [];
    private readonly ConditionalWeakTable<Page, CacheSentinel> _cacheSentinels = new();
    private int _cacheCreated;
    private static int _cacheFinalized;
    private sealed class CacheSentinel { ~CacheSentinel() => Interlocked.Increment(ref _cacheFinalized); }

    private void ObserveCachePage(Page page)
    {
        if (_cacheSentinels.TryGetValue(page, out _)) return;
        _cacheSentinels.Add(page, new CacheSentinel());
        _cachePages.Add(new(page));
        _cacheCreated++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task CycleLibrary()
    {
        _shell.NavigateToMusicBrowsePage();
        await Wait(() => _shell.CurrentLibraryPage?.IsLoaded == true && InnerFrame.Content is Page);
        ObserveCachePage(_shell.CurrentLibraryPage!);
        ObserveCachePage((Page)InnerFrame.Content);
        for (int i = 0; i < 4; i++)
        {
            foreach (string tag in new[] { "favourite", "album", "artist", "folder", "song" })
            {
                _shell.CurrentLibraryPage!.SelectBarItem(tag);
                await Task.Delay(120);
                ObserveCachePage((Page)InnerFrame.Content);
            }
        }
        _shell.ShellFrame.Navigate(typeof(EmptyPage));
        await Task.Delay(250);
    }

    private async Task CheckNavigationCacheMemory()
    {
        string name = Environment.GetEnvironmentVariable("FRAME_CACHE_RUN") ?? "unknown";
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../artifacts/frame-cache/" + name));
        Directory.CreateDirectory(output);
        for (int i = 0; i < 3; i++) await CycleLibrary();
        for (int i = 0; i < 3; i++)
        {
            await Task.Run(() => { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); });
            await Task.Delay(250);
        }
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var state = new { Created = _cacheCreated, Alive = _cachePages.Count(p => p.TryGetTarget(out _)),
            Finalized = _cacheFinalized, Managed = GC.GetTotalMemory(false), Private = process.PrivateMemorySize64,
            Handles = process.HandleCount, ForegroundViolations = _foregroundViolations,
            Pages = _cachePages.Select(p => p.TryGetTarget(out var page) ? page.GetType().Name : null).Where(x => x is not null).ToArray() };
        File.WriteAllText(Path.Combine(output, "memory.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        Check(_foregroundViolations == 0, "cache probe stays outside the foreground");
        if (name.Contains("after")) Check(state.Alive == 0 && state.Finalized == state.Created, "all retired library pages are collected with navigation cache disabled");
    }
}
