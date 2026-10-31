using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.Model;

internal sealed partial class HomePolishWindow
{
    private async Task CheckResizeStress()
    {
        await Task.Delay(500);
        _page!.CollapseDetail();
        await Wait(() => !_home.HasDetails);
        var oldSize = AppWindow.Size;
        var featured = (FeaturedCarousel)_page.FindName("Featured");
        var layout = (Grid)featured.FindName("LayoutGrid");
        var slider = (Grid)featured.FindName("SliderGrid");
        var hero = (HomeHeroCard)_page.FindName("HomeHero");
        var scroll = (ScrollViewer)_page.FindName("ContentScroll");
        var missing = new Music { Id = 990038, Path = "resize-missing.flac", Title = "No cover during resize" };
        var oldThemes = _home.Themes;
        var oldFamiliar = _home.Familiar;
        var oldDiscovery = _home.Discovery;
        var oldPaneMode = _resizeNavigationHost?.PaneDisplayMode;
        bool oldAuto = featured.AutoShuffle;
        var oldDuration = featured.ShuffleDuration;
        var footer = new RowDefinition { Height = new GridLength(128) };
        _root.RowDefinitions.Add(footer);
        typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Themes))!.SetValue(_home,
            Enumerable.Range(0, 5).Select(i => oldThemes[0] with { Artwork = i == 2 ? missing : _songs[i], Title = "为你推荐 " + i }).ToList());
        typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Familiar))!.SetValue(_home, _home.Songs.Take(8).ToList());
        typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Discovery))!.SetValue(_home, _home.Songs.Take(8).ToList());
        featured.ShuffleDuration = TimeSpan.FromMilliseconds(140);
        featured.AutoShuffle = true;
        var oldTitles = _songs.Select(m => m.Title).ToArray();
        var oldHashes = _songs.Select(m => m.ImageHash).ToArray();
        var trace = new Queue<object>();
        var changes = new List<object>();
        int step = 0, phase = 0, callbacks = 0;
        void Record(string reason)
        {
            if (trace.Count == 96) trace.Dequeue();
            trace.Enqueue(new { Step = step, Phase = phase, Reason = reason,
                Width = layout.ActualWidth, Height = layout.ActualHeight, MainWidth = slider.ActualWidth, MainHeight = slider.ActualHeight,
                Row = layout.RowDefinitions[0].Height.Value, Column = layout.ColumnDefinitions[0].Width.Value,
                scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentHeight,
                NavigationMode = _resizeNavigationHost?.DisplayMode.ToString(), PaneOpen = _resizeNavigationHost?.IsPaneOpen,
                RasterizationScale = _root.XamlRoot.RasterizationScale, SelectedIndex = featured.SelectedIndex, HeroMusic = ((MusicCoverControl)hero.FindName("Artwork")).Music?.Id });
        }
        void LayoutChanged(object sender, SizeChangedEventArgs args) { callbacks++; Record("size"); }
        void Failed(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            Record(args.Exception.GetType().Name);
            File.WriteAllText(Path.Combine(_output, "resize-failed-layout.json"), JsonSerializer.Serialize(trace));
        }
        layout.SizeChanged += LayoutChanged;
        Application.Current.UnhandledException += Failed;
        Application.Current.DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
        try
        {
            var ascending = Enumerable.Range(0, 301).Select(index => 900 + index * 2).ToArray();
            var widths = ascending.Concat(ascending.Reverse()).ToList();
            for (phase = 0; phase < 3; phase++)
            {
                if (phase > 0)
                {
                    typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Themes))!.SetValue(_home,
                        Enumerable.Range(0, 5).Select(i => oldThemes[0] with { Artwork = i == 2 ? missing : _songs[i],
                            Title = "为你推荐 / A long title for resizing and wrapping " + i,
                            Description = "熟悉的旋律与新的发现，都在你的音乐库里。A longer description to exercise adaptive height." }).ToList());
                    for (int i = 0; i < _songs.Length; i++)
                    {
                        ToolUtils.PrepareArtwork(_songs[i].Id, i % 2 == 0 ? 256 : 512, i % 2 == 0 ? 512 : 256);
                        _songs[i].ImageHash = "resize-stress-" + _songs[i].Id;
                        _songs[i].Title = "窗口缩放测试 / LongAlbumTitleWithoutWordBoundaries_" + _songs[i].Id;
                    }
                }
                if (_resizeNavigationHost is not null)
                {
                    _resizeNavigationHost.PaneDisplayMode = phase == 0 ? NavigationViewPaneDisplayMode.Auto : NavigationViewPaneDisplayMode.LeftCompact;
                    _resizeNavigationHost.IsPaneOpen = false;
                }
                _root.UseLayoutRounding = phase != 1;
                _host.Margin = phase == 1 ? new Thickness(.4) : new Thickness(0);
                ResizeStressNative.SendMessage(_hwnd, 0x0231, 0, 0); // WM_ENTERSIZEMOVE on this offscreen test window only.
                foreach (int width in phase == 2 ? widths.AsEnumerable().Reverse() : widths)
                {
                    step++;
                    int height = phase == 2 ? 730 + (step % 9) * 43 : 720 + (step % 5) * 85;
                    File.WriteAllText(Path.Combine(_output, "resize-last-step.json"), JsonSerializer.Serialize(new { Step = step, Phase = phase, Width = width, Height = height }));
                    if (phase == 2) AppWindow.MoveAndResize(new RectInt32(-30000 - step % 13, -30000 - step % 17, width, height));
                    else AppWindow.Resize(new SizeInt32(width, height));
                    await Task.Delay(8);
                    Record("settled");
                    changes.Add(trace.Last());
                }
                ResizeStressNative.SendMessage(_hwnd, 0x0232, 0, 0); // WM_EXITSIZEMOVE.
            }
            Assert(step == 1806 && callbacks < step * 12, $"continuous resize settles without layout cycles ({step} steps, {callbacks} size callbacks)");
            bool heartbeat = false;
            DispatcherQueue.TryEnqueue(() => heartbeat = true);
            await Wait(() => heartbeat);
            Assert(heartbeat, "dispatcher remains responsive after live resize, fractional layout, wrapped text and mixed cover aspect ratios");
        }
        finally
        {
            ResizeStressNative.SendMessage(_hwnd, 0x0232, 0, 0);
            layout.SizeChanged -= LayoutChanged;
            Application.Current.UnhandledException -= Failed;
            Application.Current.DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.None;
            File.WriteAllText(Path.Combine(_output, "resize-layout-history.json"), JsonSerializer.Serialize(changes));
            _root.UseLayoutRounding = true;
            _host.Margin = new Thickness(0);
            if (_resizeNavigationHost is not null && oldPaneMode is { } mode) _resizeNavigationHost.PaneDisplayMode = mode;
            featured.AutoShuffle = false;
            featured.ShuffleDuration = oldDuration;
            typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Themes))!.SetValue(_home, oldThemes);
            typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Familiar))!.SetValue(_home, oldFamiliar);
            typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.Discovery))!.SetValue(_home, oldDiscovery);
            featured.AutoShuffle = oldAuto;
            _root.RowDefinitions.Remove(footer);
            for (int i = 0; i < _songs.Length; i++)
            {
                ToolUtils.PrepareArtwork(_songs[i].Id);
                _songs[i].Title = oldTitles[i];
                _songs[i].ImageHash = oldHashes[i];
            }
            AppWindow.Resize(oldSize);
        }
    }
}

internal static class ResizeStressNative
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern nint SendMessage(nint hwnd, uint message, nuint wparam, nint lparam);
}
