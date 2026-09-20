using System.Collections.Generic;

namespace WinUIMusicPlayer.State
{
    public sealed class AppState
    {
        public Services.AppLifecycle Lifecycle { get; } = new();
        public LibraryState Library { get; } = new();
        public BrowseState Browse { get; } = new();
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed class SortOption { public object Tag { get; set; } = "DefaultOrder"; }
    public sealed class PlayList { }
    public sealed class PlayListMusicItem
    {
        public Music Music { get; set; } = new(1, "");
        public int PlayListOrder { get; set; }
    }
    public sealed record MusicGroup(string Key, List<Music> Items);
    public sealed record PlaylistLink(int MusicId, int PlayListId, int Order);
    public static class AppData { public static List<PlaylistLink> AllPlayListMusics { get; } = []; }
}
namespace WinUIMusicPlayer.Utils
{
    public static class AppSettings { public static string ArtistSplitSymbols { get; set; } = ";"; }
    public static class ArtistHelper
    {
        public static string[] GetArtistNames(string name) => name.Split(';');
        public static Model.Music CreateArtistTile(Model.Music music, string artist) => music with { Author = artist };
    }
    public static class CjkStringComparer
    {
        public static int Compare(string? left, string? right) => string.Compare(left, right, StringComparison.Ordinal);
    }
}
namespace WinUIMusicPlayer.Helper { public class Unused { } }
namespace Microsoft.UI.Xaml.Data { public sealed class CollectionViewSource { public object? Source { get; set; } } }
namespace CommunityToolkit.WinUI
{
    public static class DispatchExtensions
    {
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action work)
        {
            return dispatcher.EnqueueAsync(() => { work(); return Task.CompletedTask; });
        }
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<Task> work)
        {
            if (dispatcher.HasThreadAccess) return work();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.TryEnqueue(async () =>
            {
                try { await work(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }
    }
}
