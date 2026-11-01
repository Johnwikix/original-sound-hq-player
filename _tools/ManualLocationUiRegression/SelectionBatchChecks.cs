using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;

internal sealed partial class TestWindow
{
    private async Task CheckSelectionBatch()
    {
        await Task.Delay(500);
        var original = _app.SelectedPlaybackMusic;
        var items = new ObservableCollection<Music>(Enumerable.Range(1, 4000).Select(id => new Music { Id = id, Title = "Batch " + id }));
        var list = new ListView { ItemsSource = items, DisplayMemberPath = "Title" };
        var tracker = new PlaybackSelectionTracker(list, _app);
        Grid.SetRow(list, 1);
        _root.Children.Add(list);
        try
        {
            _app.SelectedPlaybackMusic = new Music { Id = 999999, Title = "Outside projection" };
            await Wait(() => list.IsLoaded);
            await Task.Delay(100);
            long before = Interlocked.Read(ref Music.IdentityReads);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int id = 4001; id <= 4400; id++) items.Add(new Music { Id = id, Title = "Batch " + id });
            double publishMilliseconds = watch.Elapsed.TotalMilliseconds;
            long immediateReads = Interlocked.Read(ref Music.IdentityReads) - before;
            long publicationAllocations = GC.GetAllocatedBytesForCurrentThread() - allocated;
            await Task.Delay(100);
            long totalReads = Interlocked.Read(ref Music.IdentityReads) - before;
            var result = new { Existing = 4000, Appended = 400, ImmediateReads = immediateReads, TotalReads = totalReads,
                PublishMilliseconds = publishMilliseconds, PublicationAllocatedBytes = publicationAllocations };
            File.WriteAllText(Path.Combine(Program.Output, "selection-batch.json"), JsonSerializer.Serialize(result));
            Check(immediateReads < 400 * 8 && totalReads < 4400 * 8,
                $"per-item import coalesces identity matching (immediate={immediateReads}, total={totalReads})");
            var target = new Music { Id = 4401, Title = "New playing item" };
            _app.SelectedPlaybackMusic = new Music { Id = 4401 };
            items.Add(target);
            await Wait(() => ReferenceEquals(list.SelectedItem, target));
            Check(ReferenceEquals(list.SelectedItem, target), "deferred matching selects the actual newly appended projection item");
            _app.SelectedPlaybackMusic = items[100];
            Check(ReferenceEquals(list.SelectedItem, items[100]), "real playback changes still select synchronously on UI without waiting for item batching");
            items.Add(new Music { Id = 4402 });
            _root.Children.Remove(list);
            await Task.Delay(100);
            var selected = list.SelectedItem;
            _app.SelectedPlaybackMusic = items[200];
            await Task.Delay(50);
            Check(ReferenceEquals(list.SelectedItem, selected), "unload drops queued matching and detaches external playback notifications");
            _root.Children.Add(list);
            await Wait(() => list.IsLoaded && ReferenceEquals(list.SelectedItem, items[200]));
            Check(ReferenceEquals(list.SelectedItem, items[200]), "reloaded lists resume matching current playback rather than a stale batch snapshot");
        }
        finally { _root.Children.Remove(list); _app.SelectedPlaybackMusic = original; }
    }
}
