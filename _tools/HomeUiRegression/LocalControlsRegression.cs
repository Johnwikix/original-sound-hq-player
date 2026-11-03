using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using System.Reflection;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class RegressionApplication
{
    private async Task CheckLocalControlsAsync(Window window, Music song)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Generic.xaml") });
        song.Title = "A long shipping row title that must overflow its constrained column and keep its existing hover behavior";
        song.Author = "A long shipping row artist name with multiple words";
        song.Album = "A long shipping row album name with multiple words";
        var row = new MusicListRowControl { Music = song, Width = 700 };
        var panel = new Grid { Children = { row } };
        window.Content = panel;
        await Wait(() => row.IsLoaded);
        var scrolls = Descendants<AutoScrollView>(row).ToArray();
        Check(scrolls.Length == 3 && scrolls.All(scroll => !scroll.IsPlaying), "shipping row creates three local controls with scrolling initially stopped");
        Check(ReferenceEquals(row.TitleElement, scrolls[0].Child) && ((TextBlock)scrolls[0].Child!).Text == song.Title,
            "shipping row compiled bindings populate the local title control");
        var entered = typeof(MusicListRowControl).GetMethod("AutoScrollHover_PointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var exited = typeof(MusicListRowControl).GetMethod("AutoScrollHover_PointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var canceled = typeof(MusicListRowControl).GetMethod("AutoScrollHover_PointerCanceled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sourceField = typeof(RedirectVisualView).GetField("childVisualSurface", BindingFlags.NonPublic | BindingFlags.Instance)!;
        CompositionVisualSurface Source(AutoScrollView scroll) => (CompositionVisualSurface)sourceField.GetValue(scroll)!;
        entered.Invoke(row, [scrolls[0], null]);
        await Wait(() => Source(scrolls[0]).SourceVisual is not null);
        Check(scrolls[0].IsPlaying, "shipping hover handler starts the local composition surface");
        canceled.Invoke(row, [scrolls[0], null]);
        await Wait(() => Source(scrolls[0]).SourceVisual is null);
        Check(!scrolls[0].IsPlaying, "shipping pointer cancellation stops scrolling");
        entered.Invoke(row, [scrolls[0], null]);
        await Wait(() => Source(scrolls[0]).SourceVisual is not null);
        exited.Invoke(row, [scrolls[0], null]);
        Check(!scrolls[0].IsPlaying && Source(scrolls[0]).SourceVisual is null, "shipping pointer exit restores the original XAML visual");
        foreach (var scroll in scrolls) entered.Invoke(row, [scroll, null]);
        row.Music = new Music { Id = 10001, Title = "replacement", Author = "artist", Album = "album" };
        Check(scrolls.All(scroll => !scroll.IsPlaying), "shipping recycled-row binding stops every scrolling control");
        await Wait(() => ((TextBlock)scrolls[0].Child!).Text == "replacement");
        foreach (var scroll in scrolls) entered.Invoke(row, [scroll, null]);
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        row.Unloaded += (_, _) => unloaded.TrySetResult();
        panel.Children.Remove(row);
        await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(scrolls.All(scroll => !scroll.IsPlaying && Source(scroll).SourceVisual is null), "shipping row unload clears hover state and composition sources");
        panel.Children.Add(row);
        await Wait(() => row.IsLoaded);
        Check(((TextBlock)scrolls[0].Child!).Text == "replacement" && scrolls.All(scroll => !scroll.IsPlaying),
            "shipping row reload keeps compiled bindings and stopped hover state");
    }
}
