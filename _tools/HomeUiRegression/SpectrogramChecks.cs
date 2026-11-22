using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal sealed class FixtureSpectrogramService : ISpectrogramService
{
    internal Task Gate { get; set; } = Task.CompletedTask;
    internal int Starts;
    internal SpectrogramInput? LastInput;
    internal bool Fail;
    public async Task<SpectrogramData?> AnalyzeAsync(SpectrogramInput input, int size, IProgress<double> progress, CancellationToken token)
    {
        Starts++;
        LastInput = input;
        await Gate; // 保留真实边界的不可取消收尾，用于验证关闭会等待工作完成。
        token.ThrowIfCancellationRequested();
        progress.Report(50);
        if (Fail) throw new IOException("fixture spectrum failure");
        var bytes = new byte[256 * (size / 2 + 1)];
        for (int x = 0; x < 256; x++)
        for (int y = 0; y <= size / 2; y++)
            bytes[x * (size / 2 + 1) + y] = (byte)Math.Clamp(90 + 50 * Math.Sin(x / 18d) * Math.Exp(-Math.Abs(y - 350d) / 150), 0, 160);
        return new(96000, 2, size, 256, 120, bytes, bytes, bytes);
    }
}

namespace WinUIMusicPlayer.Services
{
    public sealed class PlaybackCommands
    {
        public CommunityToolkit.Mvvm.Input.IRelayCommand<long> SeekCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand<long>(_ => { });
    }
}

internal sealed partial class RegressionApplication
{
    private async Task CheckSpectrogramAsync(Microsoft.UI.Xaml.Window window, MusicDatabaseService database)
    {
        var highest = (Windows.UI.Color)typeof(SpectrogramControl).GetMethod("Palette", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [1d, false])!;
        Check(highest.R > 220 && highest.G < 80 && highest.B < 80, "spectrum highest intensity uses a distinct red");
        var loudRegion = (Windows.UI.Color)typeof(SpectrogramControl).GetMethod("Palette", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [5d / 6, false])!;
        Check(loudRegion.R > 220 && loudRegion.G < 90, "spectrum upper intensity region already transitions into red");
        var app = new AppViewModel();
        var music = new Music { Id = 7721, Path = "fixture.wav" };
        var source = new FixtureSpectrogramService();
        var vm = new SpectrogramViewModel(music, app, source, NullLogger<SpectrogramViewModel>.Instance);
        bool offThread = false;
        vm.PropertyChanged += (_, _) => offThread |= !window.DispatcherQueue.HasThreadAccess;
        Check(source.Starts == 0, "spectrum construction starts no worker");
        vm.SetActive(true);
        await Wait(() => !vm.IsBusy);
        Check(vm.Data is { SampleRate: 96000 } && vm.Status == "", "spectrum data publishes on activation");
        var control = new SpectrogramControl { ViewModel = vm, Width = 600, Height = 420,
            RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32)) };
        ((Grid)control.Content).Background = control.Background;
        window.Content = control;
        PolishNative.ShowWithoutActivation(window);
        await Wait(() => control.IsLoaded);
        await Task.Delay(500);
        var root = (Grid)control.Content;
        var chart = (Grid)control.FindName("ChartHost");
        var transparencyButton = (ToggleButton)control.FindName("TransparencyButton");
        var optionsButton = (Button)control.FindName("OptionsButton");
        var plot = (Windows.Foundation.Rect)typeof(SpectrogramControl).GetProperty("Plot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
        double plotRight = chart.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(plot.Right, 0)).X;
        double buttonRight = optionsButton.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(optionsButton.ActualWidth, 0)).X;
        Check(Math.Abs(plotRight - buttonRight) < .1, "spectrum plot and action buttons share the same right edge");
        Check(vm.IsTransparencyEnabled && !vm.IsOpaque && transparencyButton.IsChecked == false, "spectrum transparency is enabled by default with an unchecked toggle");
        transparencyButton.IsChecked = true;
        await Wait(() => !vm.IsTransparencyEnabled);
        Check(vm.IsOpaque, "checked spectrum toggle selects opaque mode");
        transparencyButton.IsChecked = false;
        await Wait(() => vm.IsTransparencyEnabled);
        Check(((TextBlock)control.FindName("InteractionHint")).Text.Contains("Shift"), "spectrum visibly describes Shift+wheel frequency zoom");
        app.CurrentPlayingMusic = music;
        app.IsPlaying = true;
        app.CurrentPlayingTime = TimeSpan.FromSeconds(45);
        Check(vm.HasPlaybackCursor && vm.IsPlaying && vm.PositionSeconds == 45, "spectrum cursor reuses the current playback position");
        await Task.Delay(150);
        await Capture(control, "spectrum-playing.png");
        app.CurrentPlayingTime = TimeSpan.FromSeconds(45.2);
        await Task.Delay(150);
        Check(vm.IsPlaying && vm.PositionSeconds == 45.2, "continuous playback advances the composition cursor without redrawing spectral data");
        app.IsPlaying = false;
        app.CurrentPlayingTime = TimeSpan.FromSeconds(90);
        Check(!vm.IsPlaying && vm.PositionSeconds == 90, "paused spectrum observes seek without extrapolation");
        var other = new Music { Id = 7722, Path = "other.wav" };
        var originalData = vm.Data;
        int cursorChanges = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SpectrogramViewModel.HasPlaybackCursor)) cursorChanges++;
        };
        app.PreviewPlaybackMusic = other;
        app.CurrentPlayingTime = TimeSpan.FromSeconds(2);
        Check(app.CurrentPlayingMusic == music && !vm.HasPlaybackCursor && vm.PositionSeconds == 0 && cursorChanges == 1,
            "outgoing spectrum hides the cursor as soon as another song owns the displayed clock");
        app.PreviewPlaybackMusic = null;
        Check(vm.HasPlaybackCursor && cursorChanges == 2, "spectrum cursor returns when the transition display rolls back");
        app.CurrentPlayingMusic = other;
        Check(!vm.HasPlaybackCursor && vm.PositionSeconds == 0 && ReferenceEquals(vm.Data, control.ViewModel.Data),
            "switching playback hides the cursor without replacing the inspected song");
        app.PreviewPlaybackMusic = music;
        app.CurrentPlayingTime = TimeSpan.FromSeconds(3);
        Check(vm.HasPlaybackCursor && vm.PositionSeconds == 3 && cursorChanges == 4 && ReferenceEquals(originalData, vm.Data) && source.Starts == 1,
            "incoming spectrum uses the displayed position before handoff without reanalyzing the file");
        app.PreviewPlaybackMusic = null;
        vm.ZoomAt(.5, 2);
        Check(vm.Zoom == 2 && Math.Abs(vm.ViewStart - .25) < .001, "spectrum zoom keeps its anchor time");
        vm.ZoomFrequencyAt(.25, 2);
        Check(vm.VerticalZoom == 2 && Math.Abs(vm.FrequencyViewStart - .375) < .001, "spectrum frequency zoom keeps its anchor frequency");
        vm.PanFrequency(.1);
        Check(Math.Abs(vm.FrequencyViewStart - .425) < .001, "spectrum frequency pan uses the visible vertical range");
        vm.Inspect(.5, .5);
        Check(vm.Inspection.Contains("Hz") && vm.Inspection.Contains("dBFS"), "spectrum inspection exposes measured frequency and level");
        transparencyButton.IsChecked = false;
        await Task.Delay(350);
        await Capture(control, "spectrum-frequency-transparent.png");
        transparencyButton.IsChecked = true;
        await Task.Delay(350);
        vm.ResetViewCommand.Execute(null);
        Check(vm.Zoom == 1 && vm.ViewStart == 0 && vm.VerticalZoom == 1 && vm.FrequencyViewStart == 0, "spectrum reset restores both viewport axes");
        var alphaData = new SpectrogramData(48000, 1, 4, 1, 1, [40, 80, 160], [40, 80, 160], [40, 80, 160]);
        var alphaPixels = (byte[])typeof(SpectrogramControl).GetMethod("MakePixels", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [alphaData, alphaData.Combined, 3, -120d, 0d, false, true, CancellationToken.None])!;
        Check(alphaPixels[3] == 255 && alphaPixels[7] == 85 && alphaPixels[11] == 0, "spectrum transparency maps 0, -80 and -120 dBFS to increasing alpha");
        vm.MinimumDb = -80;
        vm.MaximumDb = -100;
        Check(vm.MaximumDb >= vm.MinimumDb + 10, "spectrum dynamic range stays valid");
        vm.MaximumDb = 0;
        vm.MinimumDb = -120;
        await Task.Delay(350);
        await Capture(control, "spectrum-dark.png");
        control.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Light;
        control.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        ((Grid)control.Content).Background = control.Background;
        await Task.Delay(180);
        await Capture(control, "spectrum-light.png");
        window.Content = new Grid();
        await Task.Delay(80);
        window.Content = control;
        await Task.Delay(350);
        Check(control.IsLoaded, "spectrum drawing resources recreate after unload/reload");
        Check(ReferenceEquals(((Button)control.FindName("ResetButton")).Command, vm.ResetViewCommand)
            && ReferenceEquals(((Button)control.FindName("RetryButton")).Command, vm.RetryCommand),
            "spectrum commands rebind after unload releases the native command bridges");
        vm.SetActive(false);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Gate = hold.Task;
        vm.SetActive(true);
        await Wait(() => source.Starts == 2);
        var stopping = vm.StopAsync();
        Check(!stopping.IsCompleted, "spectrum close waits for actual background work to drain");
        hold.SetResult();
        await stopping;
        Check(vm.Data is null && !vm.IsBusy && !offThread, "closed spectrum rejects late results and keeps notifications on UI thread");
        vm.SetActive(true);
        Check(source.Starts == 2, "closed spectrum cannot restart its worker");
        var errorVm = new SpectrogramViewModel(music, app, new FixtureSpectrogramService { Fail = true }, NullLogger<SpectrogramViewModel>.Instance);
        errorVm.SetActive(true);
        await Wait(() => !errorVm.IsBusy);
        Check(errorVm.Data is null && errorVm.Status.Length > 0 && errorVm.RetryCommand.CanExecute(null), "spectrum failures leave an available retry");
        await errorVm.StopAsync();

        var cue = new Music
        {
            Path = "album.wav", CuePath = "album.cue", CueTrackNo = 2, StartSample = 48000, EndSample = 96000, SampleRate = 48000
        };
        var cueService = new FixtureSpectrogramService();
        var cueVm = new SpectrogramViewModel(cue, app, cueService, NullLogger<SpectrogramViewModel>.Instance);
        cueVm.SetActive(true);
        await Wait(() => !cueVm.IsBusy);
        Check(cueService.LastInput?.SampleRange == new AudioSampleRange(48000, 96000, 48000),
            "the spectrogram view snapshots the track sample range");
        app.CurrentPlayingMusic = new Music
        {
            Path = cue.Path, CuePath = cue.CuePath, CueTrackNo = 1, StartSample = 0, EndSample = 48000, SampleRate = 48000
        };
        Check(!cueVm.HasPlaybackCursor, "a different one-shot CUE track on the same file has its own cursor");
        app.CurrentPlayingMusic = new Music
        {
            Path = cue.Path, CuePath = cue.CuePath, CueTrackNo = 2, StartSample = cue.StartSample, EndSample = cue.EndSample, SampleRate = cue.SampleRate
        };
        Check(cueVm.HasPlaybackCursor, "a re-created one-shot CUE row with the same bounds owns the cursor");
        await cueVm.StopAsync();

        var tabSource = new FixtureSpectrogramService();
        var tabVm = new SpectrogramViewModel(music, app, tabSource, NullLogger<SpectrogramViewModel>.Instance);
        var factory = new FixtureEditorFactory(database, app);
        var presentation = new MusicDetailsViewModel(factory.Create(music), app, database, factory.Sessions,
            NullLogger<MusicDetailsViewModel>.Instance, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }), null, tabVm);
        var details = new MusicDetailControl { ViewModel = presentation, Width = 900, Height = 600, ShowHeader = false };
        window.Content = details;
        await Wait(() => details.IsLoaded);
        Check(tabSource.Starts == 0, "real details pivot starts no analysis on its tags tab");
        var tabs = (Pivot)details.FindName("WorkspaceTabs");
        var originalTab = tabs.SelectedItem;
        tabs.SelectedItem = details.FindName("SpectrumTab");
        await Wait(() => tabVm.Data is not null);
        await Wait(() => Descendants<SpectrogramControl>(details).Any(control => control.IsLoaded));
        Check(ReferenceEquals(Descendants<SpectrogramControl>(details).Single().ViewModel, tabVm), "real spectrum pivot binds the existing detail view model");
        window.Content = new Grid();
        await Task.Delay(80);
        window.Content = details;
        await Wait(() => details.IsLoaded && !tabVm.IsBusy);
        Check(ReferenceEquals(((Button)details.FindName("SaveDatabaseButton")).Command, presentation.Editor.SaveDatabaseCommand),
            "detail commands restore after parent compiled tracking restarts");
        var year = (NumberBox)details.FindName("YearInput");
        year.Value = 2026;
        Check(presentation.Editor.Metadata.Draft.Year == 2026, "detail two-way inputs survive parent unload/reload");
        // The identity elements live in the ScrollViewer content's visual tree.
        var identity = Descendants<ScrollViewer>(details).Single(control => control.Name == "IdentityScroll");
        var heroText = Descendants<StackPanel>(identity).Single(control => control.Name == "HeroText");
        var actions = Descendants<StackPanel>(heroText).Single(control => control.Name == "Actions");
        var playlist = Descendants<Button>(actions).Single(control => control.Name == "PlaylistButton");
        var detailRoot = (Grid)details.FindName("DetailRoot");
        var footer = (Grid)details.FindName("FooterBar");
        Check(ReferenceEquals(actions.Parent, heroText) && Grid.GetRow(heroText) == 1
            && Bottom(actions, detailRoot) < footer.TransformToVisual(detailRoot).TransformPoint(default).Y,
            "large detail layout keeps actions inline with the identity below the artwork");
        await Capture(details, "details-large-inline-actions.png");
        presentation.Editor.Metadata.Draft.Title = "Long title 長いタイトル 长标题 " + new string('A', 180);
        details.Width = 740;
        details.Height = 420;
        await Wait(() => Grid.GetColumn(heroText) == 1 && Math.Abs(details.ActualWidth - 740) < .1);
        double playlistRight = playlist.TransformToVisual(identity).TransformPoint(new Windows.Foundation.Point(playlist.ActualWidth, 0)).X;
        Check(ReferenceEquals(actions.Parent, heroText) && playlist.ActualWidth > 0
            && Bottom(actions, identity) <= identity.ActualHeight + .1 && playlistRight <= identity.ActualWidth + .1,
            "the stacked detail layout keeps identity actions visible with a long title");
        await Capture(details, "details-long-title-inline-actions.png");

        static double Bottom(Microsoft.UI.Xaml.FrameworkElement element, Microsoft.UI.Xaml.FrameworkElement relativeTo)
            => element.TransformToVisual(relativeTo).TransformPoint(new Windows.Foundation.Point(0, element.ActualHeight)).Y;
        tabs.SelectedItem = originalTab;
        Check(!tabVm.RetryCommand.CanExecute(null), "leaving the spectrum pivot deactivates analysis");
        await presentation.StopAsync();
    }
}
