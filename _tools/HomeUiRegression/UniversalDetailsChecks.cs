using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private readonly List<WeakReference<MusicDetailsPanel>> _universalPages = [];

    private async Task CheckUniversalDetails()
    {
        // Let the external UIA invocation finish before replacing the test content.
        await Task.Delay(500);
        var frame = new Frame { CacheSize = 0 };
        Grid.SetRow(frame, 1);
        _root.Children.Add(frame);
        var navigation = App.Services.GetRequiredService<LibraryNavigationService>();
        UniversalOriginPage.Songs = _songs;
        int navigations = 0;
        frame.Navigated += (_, _) => navigations++;
        ConnectedAnimation? forwardAnimation = null, forwardTitle = null;
        bool forwardCompleted = false, titleCompleted = false;
        double sourceOpacityAtRequest = -1;
        bool sourceEnabledAtRequest = false, textEnabledAtRequest = false;
        using var detailsPresenter = new MusicDetailsPresenter(frame);
        navigation.Open = (music, sourceCover, sourceTitle) =>
        {
            forwardCompleted = titleCompleted = false;
            detailsPresenter.Show(new MusicDetailsRequest(music, typeof(UniversalOriginPage)), sourceCover, sourceTitle);
            var source = (ContentControl)((Page)frame.Content).FindName("MusicDetailsSource");
            sourceOpacityAtRequest = source.Opacity;
            sourceEnabledAtRequest = source.IsEnabled;
            var control = (MusicDetailControl)detailsPresenter.CurrentDetails!.FindName("DetailView");
            textEnabledAtRequest = ((TextBox)control.FindName("TitleInput")).IsEnabled;
        };
        navigation.Close = () => detailsPresenter.Back();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playlistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = App.Services.GetRequiredService<WinUIMusicPlayer.ViewModel.AppViewModel>();
        var database = App.Services.GetRequiredService<MusicDatabaseService>();
        var playlist = new PlayList { Id = 731, Name = "Universal test" };
        app.AllPlayList.Add(playlist);
        try
        {
            frame.Navigate(typeof(UniversalOriginPage));
            await Wait(() => frame.Content is UniversalOriginPage { IsLoaded: true });
            var origin = (UniversalOriginPage)frame.Content;
            var sourceRoot = (ContentControl)origin.FindName("MusicDetailsSource");
            int enabledChanges = 0;
            long enabledWatch = sourceRoot.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => enabledChanges++);
            var firstRow = (MusicListRowControl)origin.List.Items[20];
            origin.List.ScrollIntoView(firstRow, ScrollIntoViewAlignment.Leading);
            await Wait(() => firstRow.IsLoaded && origin.List.ContainerFromIndex(20) is not null);
            origin.UpdateLayout();
            var scroller = Descendants<ScrollViewer>(origin.List).First();
            await Wait(() => scroller.VerticalOffset > 100);
            double originalOffset = scroller.VerticalOffset;
            origin.List.SelectedIndex = 20;
            var title = (Button)firstRow.FindName("MusicTitleButton");
            Assert(ListViewInteraction.IsAction(title, origin.List), "real title button is excluded from row double-click playback");
            Assert(ListViewInteraction.IndexFromSource(title, origin.List) == 20, "title source resolves the actual queue container index");

            int reads = _editors.CoverReads;
            _editors.CoverGate = gate.Task;
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(title).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => detailsPresenter.CurrentDetails is not null && _editors.CoverReads > reads);
            Assert(ReferenceEquals(frame.Content, origin), "opening details must retain the same source page inside the same Frame");
            var firstPage = detailsPresenter.CurrentDetails!;
            _universalPages.Add(new(firstPage));
            var first = firstPage.ViewModel!;
            Assert(sourceOpacityAtRequest == 1 && sourceEnabledAtRequest && textEnabledAtRequest,
                $"entry waits for render readiness without disabling either tree: sourceOpacity={sourceOpacityAtRequest}, sourceEnabled={sourceEnabledAtRequest}, textEnabled={textEnabledAtRequest}");
            await Wait(() =>
            {
                if (forwardAnimation is null)
                {
                    forwardAnimation = ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicDetailsCover");
                    if (forwardAnimation is not null) forwardAnimation.Completed += (_, _) => forwardCompleted = true;
                }
                if (forwardTitle is null)
                {
                    forwardTitle = ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicDetailsTitle");
                    if (forwardTitle is not null) forwardTitle.Completed += (_, _) => titleCompleted = true;
                }
                return forwardAnimation is not null && forwardTitle is not null;
            });
            var titleInput = (TextBox)((MusicDetailControl)firstPage.FindName("DetailView")).FindName("TitleInput");
            Assert(titleInput.IsLoaded && titleInput.ActualHeight > 0 && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(titleInput) > 0
                && titleInput.IsReadOnly && titleInput.IsEnabled,
                "entry renders the real TextBox template while keeping a loading draft read-only and visually enabled");
            Assert(forwardAnimation is not null && forwardTitle is not null,
                "row cover and title both prepare real connected animations");
            await Wait(() => forwardCompleted && titleCompleted);
            Assert(ReferenceEquals(first.Music, _songs[20]), "actual row title opens a real universal detail page for the clicked song");
            Assert(frame.BackStack.Count == 0 && frame.ForwardStack.Count == 0,
                "detail presentation does not create Frame navigation history");
            database.PlaylistGate = playlistGate.Task;
            first.PreparePlaylistMenuCommand.Execute(null);
            var addMenu = first.PlaylistMenuItems.First(item => item.Title == playlist.Name);
            var playlistWrite = ((IAsyncRelayCommand)addMenu.Command).ExecuteAsync(addMenu.Tag);
            navigation.CloseSongDetails();
            ConnectedAnimation? backCover = null, backTitle = null;
            bool coverBackDone = false, titleBackDone = false;
            await Wait(() =>
            {
                if (backCover is null)
                {
                    backCover = ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicDetailsCover");
                    if (backCover is not null) backCover.Completed += (_, _) => coverBackDone = true;
                }
                if (backTitle is null)
                {
                    backTitle = ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicDetailsTitle");
                    if (backTitle is not null) backTitle.Completed += (_, _) => titleBackDone = true;
                }
                return backCover?.Configuration is DirectConnectedAnimationConfiguration && backTitle?.Configuration is DirectConnectedAnimationConfiguration;
            });
            Assert(backCover?.Configuration is DirectConnectedAnimationConfiguration && backTitle?.Configuration is DirectConnectedAnimationConfiguration,
                "back prepares direct connected animations for both cover and title");
            Assert(!first.StopAsync().IsCompleted, "leaving universal details waits for the actual uncooperative cover reader");

            _editors.CoverGate = Task.CompletedTask;
            await Wait(() => !detailsPresenter.IsOpen && coverBackDone && titleBackDone);
            Assert(ReferenceEquals(frame.Content, origin) && ReferenceEquals(origin.List.Items[20], firstRow) && navigations == 1,
                "back preserves the original page and row with zero additional Frame navigations");
            Assert(Math.Abs(scroller.VerticalOffset - originalOffset) < 1 && origin.List.SelectedIndex == 20,
                $"return preserves scrolled viewport and selection: before={originalOffset}, after={scroller.VerticalOffset}, selected={origin.List.SelectedIndex}");
            Assert(enabledChanges == 0 && sourceRoot.IsEnabled,
                "both directions leave the entire source tree enabled without disabled/normal button transitions");
            sourceRoot.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, enabledWatch);
            var secondRow = (MusicListRowControl)((UniversalOriginPage)frame.Content).List.Items[21];
            var secondTitle = (Button)secondRow.FindName("MusicTitleButton");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(secondTitle).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => detailsPresenter.CurrentDetails?.ViewModel is { Editor.IsLoading: false });
            var secondPage = detailsPresenter.CurrentDetails!;
            _universalPages.Add(new(secondPage));
            var second = secondPage.ViewModel!;
            Assert(!ReferenceEquals(first, second) && !ReferenceEquals(first.Editor, second.Editor),
                "different entries own independent presentation and edit sessions");
            gate.TrySetResult();
            await Wait(() => !first.Editor.IsLoading);
            Assert(!first.StopAsync().IsCompleted && !playlistWrite.IsCompleted,
                "closed presentation retains its stop barrier until the accepted playlist write completes");
            playlistGate.TrySetResult();
            await first.StopAsync();
            Assert(await database.GetDbConnection().ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM PlayListMusic WHERE PlayListId = 731 AND MusicId = ?", _songs[20].Id) == 1,
                "playlist write keeps the clicked song identity after a different detail page opens");
            Assert(ReferenceEquals(second.Music, _songs[21]) && second.Editor.CoverPreview is not null && first.Editor.CoverPreview is null,
                "late cover completion from the previous page cannot overwrite the next session");

            var editor = second.Editor;
            secondPage.UpdatePlaybackContext(new MusicDetailsRequest(_songs[21], null, 98765));
            var detailView = (MusicDetailControl)secondPage.FindName("DetailView");
            var year = (NumberBox)detailView.FindName("YearInput");
            Assert(new[] { "YearInput", "DiskInput", "TrackInput", "OffsetInput" }
                .All(name => ((NumberBox)detailView.FindName(name)).SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Inline),
                "detail numeric fields use inline spin buttons rather than compact popups");
            double oldYear = year.Value;
            year.Value = 2026;
            Assert(second.Editor.Metadata.Draft.Year == 2026, "inline NumberBox retains two-way draft binding");
            year.Value = oldYear;
            var play = (Button)detailView.FindName("PlayButton");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(play).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => MusicCommands.LastDetailsPlay?.QueueEntryId == 98765);
            Assert(ReferenceEquals(editor, second.Editor) && MusicCommands.LastDetailsPlay?.QueueEntryId == 98765,
                "same-song queue context updates preserve the draft and exact duplicate-entry identity");
            await Wait(() => detailView.IsLoaded && detailView.ActualHeight > 100 && detailView.ActualWidth > 100);
            await Task.Delay(600);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(detailView);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            Assert(pixels.Any(value => value != 0), "universal detail layout renders visible content in the native window");
            using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "home-polish-universal.png")))
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight,
                    96, 96, pixels);
                await encoder.FlushAsync();
            }

            second.Editor.Metadata.Draft.Title = "Universal saved title";
            await second.Editor.SaveDatabaseCommand.ExecuteAsync(null);
            await Wait(() => !detailsPresenter.IsOpen);
            Assert(ReferenceEquals(frame.Content, origin) && navigations == 1, "saving also closes in place without reentering a Frame");
            await second.StopAsync();
            var saved = await App.Services.GetRequiredService<MusicDatabaseService>().GetDbConnection().GetAsync<Music>(_songs[21].Id);
            Assert(saved.Title == "Universal saved title" && second.Editor.CoverPreview is null,
                "universal save uses the real SQLite editor pipeline, returns, and releases cover data");
        }
        finally
        {
            gate.TrySetResult();
            _editors.CoverGate = Task.CompletedTask;
            playlistGate.TrySetResult();
            database.PlaylistGate = Task.CompletedTask;
            app.AllPlayList.Remove(playlist);
            detailsPresenter.Dismiss();
            frame.Content = null;
            navigation.Open = null;
            navigation.Close = null;
            _root.Children.Remove(frame);
        }
    }
}

internal sealed partial class HomePolishWindow
{
    private async Task CheckDetailsTiming()
    {
        var frame = new Frame { CacheSize = 0 };
        Grid.SetRow(frame, 1);
        _root.Children.Add(frame);
        using var presenter = new MusicDetailsPresenter(frame);
        var timeline = new List<object>();
        string phase = "setup";
        ContentControl? source = null, host = null;
        int rendered = 0;
        EventHandler<object> rendering = (_, _) =>
        {
            rendered++;
            timeline.Add(new { Phase = phase, Frame = rendered, SourceOpacity = source?.Opacity,
                SourceEnabled = source?.IsEnabled, HostOpacity = host?.Opacity,
                DetailsLoaded = presenter.CurrentDetails?.IsLoaded });
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            UniversalOriginPage.Songs = _songs;
            frame.Navigate(typeof(UniversalOriginPage));
            await Wait(() => frame.Content is UniversalOriginPage { IsLoaded: true });
            var origin = (UniversalOriginPage)frame.Content;
            var row = (MusicListRowControl)origin.List.Items[0];
            await Wait(() => row.IsLoaded);
            source = (ContentControl)origin.FindName("MusicDetailsSource");
            host = (ContentControl)origin.FindName("MusicDetailsOverlay");
            var request = new MusicDetailsRequest(_songs[0], typeof(UniversalOriginPage));

            phase = "cancel-before-render";
            presenter.Show(request, row.CoverElement, row.TitleElement);
            _universalPages.Add(new(presenter.CurrentDetails!));
            int closed = 0;
            presenter.Back(() => closed++);
            await Task.Delay(100);
            Assert(closed == 1 && !presenter.IsOpen && source.Opacity == 1 && source.IsEnabled,
                "back before the first rendered frame cancels preparation and cannot resurrect a details panel");

            phase = "replace-before-render";
            presenter.Show(request, row.CoverElement, row.TitleElement);
            _universalPages.Add(new(presenter.CurrentDetails!));
            presenter.Show(new MusicDetailsRequest(_songs[1], typeof(UniversalOriginPage)));
            _universalPages.Add(new(presenter.CurrentDetails!));
            Assert(ReferenceEquals(presenter.CurrentDetails?.Music, _songs[1]) && source.Opacity == 1 && source.IsEnabled,
                "replacing a pending details request retains visible source content while preparing the new session");
            presenter.Back();
            await Task.Delay(100);
            Assert(!presenter.IsOpen && host.Content is null, "only the newest preparation can commit after a replacement is cancelled");

            phase = "enter";
            int before = rendered;
            presenter.Show(request, row.CoverElement, row.TitleElement);
            _universalPages.Add(new(presenter.CurrentDetails!));
            Assert(source.Opacity == 1 && host.Opacity < .01 && !source.IsHitTestVisible && !host.IsHitTestVisible,
                "prewarming retains the source while both trees reject pointer input");
            await Wait(() => host.Opacity == 1);
            Assert(rendered - before >= 2 && presenter.CurrentDetails is { IsLoaded: true, ActualWidth: > 0 },
                "forward handoff waits for two real CompositionTarget.Rendering callbacks");
            phase = "reverse-during-entry";
            presenter.Back();
            await Wait(() => !presenter.IsOpen);
            Assert(ReferenceEquals(frame.Content, origin) && source.Opacity == 1 && source.IsHitTestVisible && source.IsEnabled,
                "reversing an in-progress entry restores the same source tree");

            phase = "detach-before-render";
            presenter.Show(request, row.CoverElement, row.TitleElement);
            _universalPages.Add(new(presenter.CurrentDetails!));
            _root.Children.Remove(frame);
            await Wait(() => !presenter.IsOpen);
            await Task.Delay(100);
            Assert(host.Content is null && source.Opacity == 1 && source.IsEnabled,
                "unloading the owner during render preparation cancels pending callbacks and releases the overlay");
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            File.WriteAllText(Path.Combine(_output, "detail-render-timeline.json"), System.Text.Json.JsonSerializer.Serialize(timeline));
            presenter.Dismiss();
            frame.Content = null;
            _root.Children.Remove(frame);
        }
    }
}
