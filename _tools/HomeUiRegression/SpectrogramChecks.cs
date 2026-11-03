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
    internal bool Fail;
    public async Task<SpectrogramData?> AnalyzeAsync(SpectrogramInput input, int size, IProgress<double> progress, CancellationToken token)
    {
        Starts++;
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
        app.CurrentPlayingMusic = new Music { Id = 7722, Path = "other.wav" };
        Check(!vm.HasPlaybackCursor && vm.PositionSeconds == 0 && ReferenceEquals(vm.Data, control.ViewModel.Data),
            "switching playback hides the cursor without replacing the inspected song");
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
        tabs.SelectedIndex = 3;
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
        var identityPanel = (Grid)details.FindName("IdentityPanel");
        var actions = (StackPanel)details.FindName("Actions");
        var playlist = (Button)details.FindName("PlaylistButton");
        var detailRoot = (Grid)details.FindName("DetailRoot");
        double actionsBottom = actions.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(0, actions.ActualHeight)).Y;
        double panelBottom = identityPanel.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(0, identityPanel.ActualHeight)).Y;
        Check(panelBottom - actionsBottom > 40, "large detail layout keeps actions next to identity content instead of pinning them to the window");
        await Capture(details, "details-large-inline-actions.png");
        presentation.Editor.Metadata.Draft.Title = "Long title 長いタイトル 长标题 " + new string('A', 180);
        details.Width = 760;
        details.Height = 420;
        await Task.Delay(250);
        actionsBottom = actions.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(0, actions.ActualHeight)).Y;
        panelBottom = identityPanel.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(0, identityPanel.ActualHeight)).Y;
        double playlistRight = playlist.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(playlist.ActualWidth, 0)).X;
        double panelRight = identityPanel.TransformToVisual(detailRoot).TransformPoint(new Windows.Foundation.Point(identityPanel.ActualWidth, 0)).X;
        Check(ReferenceEquals(actions.Parent, identityPanel) && actionsBottom <= panelBottom + .1 && playlistRight <= panelRight + .1,
            "long titles keep all detail actions visible in a fixed compact-layout footer");
        await Capture(details, "details-long-title-fixed-actions.png");
        tabs.SelectedIndex = 0;
        Check(!tabVm.RetryCommand.CanExecute(null), "leaving the spectrum pivot deactivates analysis");
        await presentation.StopAsync();
    }
}
