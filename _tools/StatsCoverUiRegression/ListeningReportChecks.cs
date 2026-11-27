using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using AnimatedWin2dControls;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SQLite;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer;

internal static class ListeningReportChecks
{
    private static BackgroundShaderMode _configuredShader = BackgroundShaderMode.GradientFlow;
    private static int _settingsReads;
    private static Func<string, CancellationToken, Task<string?>>? _picker;

    internal static (BackgroundShaderMode, PaletteAlgorithm) ReadSettings()
    {
        _settingsReads++;
        return (_configuredShader, PaletteAlgorithm.KMeansPP);
    }

    internal static Task<string?> PickPathAsync(string name, CancellationToken token) =>
        _picker?.Invoke(name, token) ?? Task.FromResult<string?>(null);

    internal static async Task RunAsync(Frame frame, SQLiteAsyncConnection db, AppViewModel shell, ListeningReportViewModel vm,
        string directory, string cover, Action<bool, string> check, Action<string> record)
    {
        directory = Path.Combine(directory, "listening-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (Environment.GetEnvironmentVariable("REPORT_PREVIEW_FOCUS") != "1")
            await Task.Run(() => QueryChecks(Path.Combine(directory, "reports.db"), check, record));
        await ShaderChecksAsync(check);

        const string hash = "ReportTest";
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_PlaybackHistory_StartedAt ON PlaybackHistory(StartedAt)");
        await db.InsertAsync(new Music { Id = 1234, SongId = 11, ImageHash = hash });
        await db.InsertAsync(new PlaybackHistory
        {
            MusicId = 1234, SongIdAtPlay = 11, Title = "Poster fixture track", Author = "Poster fixture artist",
            StartedAt = DateTime.UtcNow.AddMinutes(-5), EndedAt = DateTime.UtcNow.AddMinutes(-2),
            TotalDurationMs = 180000, DurationPlayedMs = 180000
        });
        int previousYear = DateTime.Now.Year - 1;
        await db.ExecuteAsync("""
            INSERT INTO PlaybackHistory (MusicId, SongIdAtPlay, Title, Author, Album, TotalDurationMs, DurationPlayedMs, StartedAt, EndedAt)
            VALUES (91234,12000,'Older retained track','','',180000,180000,?,?)
            """, new DateTime(previousYear, 1, 2, 12, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(previousYear, 1, 2, 12, 3, 0, DateTimeKind.Utc).Ticks);
        string path = WebDavCachePaths.FindCover(AppSettings.MusicCoverCache, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(cover, path, overwrite: true);

        double scale = frame.XamlRoot.RasterizationScale;
        App.MainWindow.AppWindow.ResizeClient(new((int)(890 * scale), (int)(560 * scale)));
        var statistics = (StatsPage)frame.Content;
        var statisticsTitle = Find<TextBlock>(statistics, text => text.Text == "Statistics")!;
        var statisticsScroll = (ScrollViewer)statistics.FindName("StatsScroll");
        await UntilAsync(() => Math.Abs(frame.ActualHeight - 560) < 2 && statisticsScroll.ScrollableHeight >= 32);
        statisticsScroll.ChangeView(null, 32, null, disableAnimation: true);
        await UntilAsync(() => statisticsScroll.VerticalOffset > 0);
        double statisticsOffset = statisticsScroll.VerticalOffset;
        int statisticsRange = statistics.ViewModel.SelectedTimeRangeIndex;
        check(!shell.IsBackBtnEnable, "statistics root leaves the shell back button disabled");
        var entry = FindButton(statistics, "Listening reports") ?? throw new InvalidOperationException("Localised report entry missing.");
        Invoke(entry);
        await UntilAsync(() => frame.Content is ListeningReportPage && vm.Presentation is not null && !vm.IsLoading);
        var page = (ListeningReportPage)frame.Content;
        check(shell.IsBackBtnEnable && page.CanNavigateBack, "report adds Frame history and enables shell back");
        var summary = (Border)page.FindName("SummaryCard");
        var trend = (Border)page.FindName("TrendCard");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            page.RequestedTheme = theme;
            await Task.Delay(30);
            check(summary.Background is SolidColorBrush summaryBrush &&
                trend.Background is SolidColorBrush trendBrush &&
                summaryBrush.Color == trendBrush.Color && summaryBrush.Color.A < 255 &&
                summary.BorderThickness == trend.BorderThickness,
                $"report summary and trend use the same translucent {theme} card theme");
        }
        var reportTitle = (TextBlock)page.FindName("ReportTitle");
        check(reportTitle.FontSize == statisticsTitle.FontSize &&
            reportTitle.FontWeight.Equals(statisticsTitle.FontWeight) &&
            reportTitle.TransformToVisual(page).TransformPoint(default) ==
            statisticsTitle.TransformToVisual(statistics).TransformPoint(default),
            "report and statistics headings use matching typography and page offsets");
        check(vm.ModeIndex == 0 && vm.Presentation!.Tiles.Length == 12, "statistics entry opens the compiled annual report");
        App.MainWindow.AppWindow.ResizeClient(new((int)(1320 * scale), (int)(640 * scale)));
        await UntilAsync(() => frame.ActualWidth > 1000 && summary.ActualWidth > 960);
        var habits = (Border)page.FindName("HabitsCard");
        var habitItems = Find<ItemsRepeater>(habits);
        check(summary.ActualWidth >= page.ActualWidth - 56 && summary.CornerRadius.TopLeft == 4 &&
            Math.Abs(trend.TransformToVisual(summary).TransformPoint(default).Y - summary.ActualHeight - 8) < 1 &&
            ReferenceEquals(habitItems?.ItemsSource, vm.Presentation?.Habits),
            "expanded report uses the available width and keeps compact cards and compiled habit metrics");
        var songSection = (Border)page.FindName("SongSection");
        var artistSection = (Border)page.FindName("ArtistSection");
        var songs = Find<ItemsControl>(songSection)!;
        var artists = Find<ItemsControl>(artistSection)!;
        await UntilAsync(() => Grid.GetColumn(artistSection) == 1 &&
            songs.ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 } &&
            artists.ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 });
        var songRow = Find<Grid>(songs.ContainerFromIndex(0))!;
        var artistRow = Find<Grid>(artists.ContainerFromIndex(0))!;
        var topSongNote = Find<TextBlock>(page, text => text.Text == vm.Presentation?.TopSongNote);
        check(Math.Abs(songSection.ActualHeight - artistSection.ActualHeight) < 1 &&
            Math.Abs(songRow.TransformToVisual(artistRow).TransformPoint(default).Y) < 1 &&
            Math.Abs(songRow.ActualHeight - artistRow.ActualHeight) < 1 &&
            songRow.Children.Count == 3 && songRow.Children.All(child => child is TextBlock) &&
            Find<TextBlock>(songSection, text => text.Text == "Top tracks") is { TextWrapping: TextWrapping.NoWrap } &&
            Find<TextBlock>(artistSection, text => text.Text == "Top artists") is { TextWrapping: TextWrapping.NoWrap } &&
            topSongNote is not null && topSongNote.TransformToVisual(songSection).TransformPoint(default).Y > songSection.ActualHeight,
            "wide rankings share height and row alignment, with single-line tracks and the highlight below");
        App.MainWindow.AppWindow.ResizeClient(new((int)(720 * scale), (int)(560 * scale)));
        await UntilAsync(() => frame.ActualWidth < 800 && Grid.GetRow(artistSection) == 1);
        check(Math.Abs(artistSection.ActualWidth - songSection.ActualWidth) < 1 &&
            Math.Abs(artistSection.TransformToVisual(songSection).TransformPoint(default).Y - songSection.ActualHeight - 8) < 1,
            "narrow rankings stack at full width with the same compact spacing");
        var radio = Find<RadioButtons>(page) ?? throw new InvalidOperationException("Period control missing.");
        radio.SelectedIndex = 1;
        await UntilAsync(() => !vm.IsLoading && vm.Presentation?.Weekdays.Length == 7);
        check(vm.ModeIndex == 1 && vm.Presentation!.PlayCount == "2", "compiled natural-month selection updates counts");
        vm.SelectedYear = previousYear;
        vm.SelectedMonth = 1;
        vm.SelectedMonth = 12;
        await UntilAsync(() => !vm.IsLoading && vm.SelectedMonth == 12 && vm.StatusText.Length > 0);
        check(vm.Presentation is null && !vm.CanPreview && vm.OpenRecentCommand.CanExecute(null),
            "fast period switching shows the latest empty period and offers recovery");
        Invoke(FindButton(page, "View latest period with records")!);
        await UntilAsync(() => !vm.IsLoading && vm.Presentation?.PlayCount == "2");

        // Substitute only the path picker. Frame history, compiled bindings, capture and encoding use production code.
        string png = Path.Combine(directory, "listening-report.png");
        int pickerCalls = 0;
        string? pickedPath = null;
        string? suggested = null;
        bool controlsLockedDuringSave = false;
        int reportBackStackDepth = frame.BackStackDepth;

        var standardDialog = new ContentDialog
        {
            XamlRoot = frame.XamlRoot,
            Content = new Border { Height = 640 },
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Dismiss",
            DefaultButton = ContentDialogButton.Close
        };
        var standardOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        standardDialog.Opened += (_, _) => standardOpened.TrySetResult();
        var standardResult = standardDialog.ShowWithLayoutAsync();
        await standardOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => FindDialogButton("Dismiss") is not null);
        await CheckDialogLayoutAsync("Dismiss");
        Invoke(DialogButton("Dismiss"));
        check(await standardResult.AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == ContentDialogResult.None, "standard default close button still dismisses the modal");

        var reportScroll = (ScrollViewer)page.FindName("ReportScroll");
        await UntilAsync(() => reportScroll.ScrollableHeight > 0);
        reportScroll.ChangeView(null, 32, null, disableAnimation: true);
        await UntilAsync(() => reportScroll.VerticalOffset > 0);
        double reportOffset = reportScroll.VerticalOffset;
        string reportPeriod = $"{vm.SelectedYear:0000}-{vm.SelectedMonth:00}";
        var reportSnapshot = vm.Presentation;

        var preview = await OpenPreviewAsync();
        var shaderPicker = (ComboBox)preview.FindName("ShaderPicker");
        var viewport = (Grid)preview.FindName("PreviewViewport");
        var canvas = Find<Grid>((FrameworkElement)preview.Content, grid => grid.Name == "PosterCanvas")!;
        var posterCover = Find<Border>((FrameworkElement)preview.Content, border => border.Name == "PosterCover")!;
        await UntilAsync(() => viewport.ActualHeight > 0 && canvas.ActualHeight > 0);
        var posterBounds = canvas.TransformToVisual(viewport).TransformBounds(new(0, 0, canvas.ActualWidth, canvas.ActualHeight));
        check(Math.Abs(canvas.ActualWidth - 540) < 1 && Math.Abs(canvas.ActualHeight - 675) < 1 &&
            posterBounds.Width > 0 && posterBounds.Height > 0 &&
            Math.Abs(posterBounds.X * 2 + posterBounds.Width - viewport.ActualWidth) < 2 &&
            Math.Abs(posterBounds.Y * 2 + posterBounds.Height - viewport.ActualHeight) < 2 &&
            posterBounds.Height <= viewport.ActualHeight + 1 && posterBounds.Width <= viewport.ActualWidth + 1 &&
            Math.Abs(posterBounds.Width / posterBounds.Height - 0.8) < 0.01 &&
            posterCover.CornerRadius.TopLeft == 12 && posterCover.Background is ImageBrush coverBrush &&
            vm.PreviewCover is not null && ReferenceEquals(coverBrush.ImageSource, vm.PreviewCover),
            "constrained preview fits the entire centered poster and binds the rounded cover brush");
        check(ReferenceEquals(frame.Content, page) && frame.BackStackDepth == reportBackStackDepth &&
            Equals(preview.Title, "Summary image preview") && preview.CloseButtonText == "Close",
            "export opens a localized modal over the same report without adding navigation history");
        await CheckDialogLayoutAsync("Close");
        App.MainWindow.AppWindow.ResizeClient(new((int)(720 * scale), (int)(300 * scale)));
        await UntilAsync(() => Math.Abs(frame.ActualHeight - 300) < 2 && viewport.ActualHeight < posterBounds.Height - 100);
        await CheckDialogLayoutAsync("Close");
        App.MainWindow.AppWindow.ResizeClient(new((int)(1120 * scale), (int)(820 * scale)));
        await UntilAsync(() => viewport.ActualHeight > posterBounds.Height + 100);
        var tallBounds = canvas.TransformToVisual(viewport).TransformBounds(new(0, 0, canvas.ActualWidth, canvas.ActualHeight));
        check(tallBounds.Height > posterBounds.Height + 100 && tallBounds.Height <= viewport.ActualHeight + 1 &&
            Math.Abs(tallBounds.Width / tallBounds.Height - 0.8) < 0.01,
            "resizing the open dialog enlarges the portrait proportionally within the available space");
        App.MainWindow.AppWindow.ResizeClient(new((int)(720 * scale), (int)(560 * scale)));
        await UntilAsync(() => Math.Abs(viewport.ActualHeight - posterBounds.Height) < 2);
        string[] shaderNames = ["Fluid", "PS3 XMB", "Rotating Mesh", "Liquid Flow", "Gradient Flow", "Wavy Background", "Color Bands"];
        check(shaderPicker.Items.Count == Enum.GetValues<BackgroundShaderMode>().Length &&
            shaderPicker.Items.Cast<ComboBoxItem>().Select(item => item.Content).SequenceEqual(shaderNames) &&
            shaderPicker.SelectedIndex == (int)BackgroundShaderMode.GradientFlow && _settingsReads == 1,
            "compiled picker lists the seven localized styles and defaults from settings on entry");
        var initialBackground = vm.PreviewBackground;
        var initialCover = vm.PreviewCover;
        int backgroundChanges = 0;
        vm.PropertyChanged += CountBackground;
        shaderPicker.SelectedIndex = (int)BackgroundShaderMode.RotatingMesh;
        shaderPicker.SelectedIndex = (int)BackgroundShaderMode.LiquidFlow;
        shaderPicker.SelectedIndex = (int)BackgroundShaderMode.FluidBackground;
        await UntilAsync(() => vm.SaveCommand.CanExecute(null) && vm.PreviewBackground is not null);
        vm.PropertyChanged -= CountBackground;
        check(backgroundChanges == 1 && !ReferenceEquals(initialBackground, vm.PreviewBackground) &&
            initialCover is not null && ReferenceEquals(initialCover, vm.PreviewCover) &&
            vm.SelectedShaderIndex == (int)BackgroundShaderMode.FluidBackground && _configuredShader == BackgroundShaderMode.GradientFlow && _settingsReads == 1,
            "rapid picker changes publish only the latest frame, reuse the cover, and leave settings untouched");

        _picker = (name, token) =>
        {
            pickerCalls++;
            suggested = name;
            int selected = vm.SelectedShaderIndex;
            vm.SelectedShaderIndex = (int)BackgroundShaderMode.PS3XMB;
            page.NavigateBack();
            controlsLockedDuringSave = ReferenceEquals(page.PreviewDialog, preview) && !preview.IsPrimaryButtonEnabled &&
                !shaderPicker.IsEnabled && selected == vm.SelectedShaderIndex;
            return Task.FromResult(pickedPath);
        };

        var saveButton = DialogButton("Save PNG");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 1 && !vm.IsSaving);
        check(vm.IsPreview && !File.Exists(png) && vm.SaveStatus.Length == 0 && controlsLockedDuringSave &&
            shell.IsBackBtnEnable && shaderPicker.IsEnabled, "saving locks the shader and back action until the picker is cancelled");

        pickedPath = Path.Combine(directory, "absent", "report.png");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 2 && !vm.IsSaving);
        check(vm.SaveStatus.Contains("Couldn’t save", StringComparison.Ordinal), "PNG write failure reports an error and leaves retry available");

        long previewSignature;
        using (var bitmap = await preview.CaptureAsync(default))
            previewSignature = SamplePoster(bitmap);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBefore = process.PrivateMemorySize64;
        long managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        double rasterization = preview.XamlRoot.RasterizationScale;
        pickedPath = png;
        File.WriteAllText(png, "previous file");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 3 && !vm.IsSaving);
        watch.Stop();
        process.Refresh();
        record($"PNG save at {rasterization:P0} scale: {watch.Elapsed.TotalMilliseconds:F0} ms, managed process allocation delta " +
            $"{GC.GetTotalAllocatedBytes(precise: true) - managedBefore:N0} B, private bytes change at completion {process.PrivateMemorySize64 - privateBefore:N0} B.");
        check(vm.SaveStatus.Contains(png, StringComparison.Ordinal) && File.Exists(png) &&
            suggested == "OriginalSound-Listening-" + reportPeriod && !Directory.GetFiles(directory, "*.part").Any(),
            "summary PNG replaces the existing file and cleans temporary output");
        using (var file = File.OpenRead(png))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            check(decoder.PixelWidth == 1080 && decoder.PixelHeight == 1350, "real PNG decodes to 1080 by 1350");
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            check(SamplePoster(bitmap) == previewSignature, "PNG and the selected preview have matching shader pixels");
        }

        shaderPicker.SelectedIndex = (int)BackgroundShaderMode.RotatingMesh;
        page.NavigateBack();
        await UntilAsync(() => page.PreviewDialog is null && !vm.IsPreview);
        check(ReferenceEquals(page, frame.Content) && frame.BackStackDepth == reportBackStackDepth &&
            Math.Abs(reportScroll.VerticalOffset - reportOffset) < 1 &&
            shell.IsBackBtnEnable && ReferenceEquals(reportSnapshot, vm.Presentation) && vm.ModeIndex == 1 &&
            vm.PreviewCover is null && vm.PreviewBackground is null,
            "back dismisses the dialog, cancels its render and retains the report period, snapshot and scroll");

        _configuredShader = BackgroundShaderMode.PS3XMB;
        preview = await OpenPreviewAsync();
        shaderPicker = (ComboBox)preview.FindName("ShaderPicker");
        check(_settingsReads == 2 && shaderPicker.SelectedIndex == (int)BackgroundShaderMode.PS3XMB && vm.PreviewBackground is not null,
            "reopening discards the temporary shader and reads the latest settings");
        Invoke(DialogButton("Close"));
        await UntilAsync(() => page.PreviewDialog is null);
        // Leave time for composition teardown before recording retained references and process memory.
        var closedPreviews = new List<WeakReference[]>();
        await Task.Delay(400);
        RecordClosedMemory("baseline", 0);
        for (int cycle = 1; cycle <= 12; cycle++)
        {
            closedPreviews.Add(await CyclePreviewAsync(save: false));
            await Task.Delay(400);
            RecordClosedMemory("preview", cycle);
        }
        for (int cycle = 1; cycle <= 20; cycle++)
        {
            closedPreviews.Add(await CyclePreviewAsync(save: true));
            await Task.Delay(400);
            RecordClosedMemory("export", cycle);
        }
        await Task.Delay(8000);
        RecordClosedMemory("idle", 0);
        int retained = closedPreviews.SelectMany(static cycle => cycle).Count(static reference => reference.IsAlive);
        check(retained == 0, $"repeated shader preview and PNG capture release all dialog, poster and image wrappers ({retained} retained)");

        if (Environment.GetEnvironmentVariable("REPORT_RENDER_PROBE") == "1")
            await CompareRenderTargetAsync();

        page = (ListeningReportPage)frame.Content;
        vm.SelectedYear = previousYear;
        vm.SelectedMonth = 1;
        await UntilAsync(() => !vm.IsLoading && vm.Presentation?.PosterSongs == "Older retained track");
        preview = await OpenPreviewAsync();
        check(vm.PreviewCover is null && vm.PreviewBackground is null, "missing retained cover uses the poster fallback");
        Invoke(DialogButton("Close"));
        await UntilAsync(() => page.PreviewDialog is null);
        ((ListeningReportPage)frame.Content).NavigateBack();
        await UntilAsync(() => frame.Content is StatsPage);
        var returned = (StatsPage)frame.Content;
        await UntilAsync(() => Math.Abs(((ScrollViewer)returned.FindName("StatsScroll")).VerticalOffset - statisticsOffset) < 1);
        check(vm.Presentation is null && !shell.IsBackBtnEnable && returned.ViewModel.SelectedTimeRangeIndex == statisticsRange,
            "shell back restores statistics period and scroll and clears report ownership");

        void CountBackground(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ListeningReportViewModel.PreviewBackground)) backgroundChanges++;
        }

        async Task CompareRenderTargetAsync()
        {
            var root = (Grid)page.Content;
            var host = new Canvas();
            var surface = AddBorder();
            root.Children.Add(host);
            try
            {
                await UntilAsync(() => surface.IsLoaded && surface.ActualWidth > 0);
                var bitmap = new RenderTargetBitmap();
                int width = (int)surface.Width;
                int height = (int)surface.Height;
                for (int cycle = 1; cycle <= 8; cycle++)
                {
                    await bitmap.RenderAsync(surface, width, height);
                    await Task.Delay(400);
                    RecordClosedMemory("render", cycle);
                }
                for (int cycle = 1; cycle <= 8; cycle++)
                {
                    await bitmap.RenderAsync(surface, width, height);
                    var pixels = await bitmap.GetPixelsAsync();
                    check(pixels.Length == bitmap.PixelWidth * bitmap.PixelHeight * 4,
                        "isolated solid border produces a valid render target readback");
                    await Task.Delay(400);
                    RecordClosedMemory("readback", cycle);
                }
                host.Children.Remove(surface);
                for (int cycle = 1; cycle <= 8; cycle++)
                {
                    surface = AddBorder();
                    await UntilAsync(() => surface.IsLoaded && surface.ActualWidth > 0);
                    await bitmap.RenderAsync(surface, width, height);
                    _ = await bitmap.GetPixelsAsync();
                    host.Children.Remove(surface);
                    await Task.Delay(400);
                    RecordClosedMemory("remount", cycle);
                }
            }
            finally
            {
                root.Children.Remove(host);
            }

            Border AddBorder()
            {
                var border = new Border
                {
                    Width = ListeningReportImages.Width / scale,
                    Height = ListeningReportImages.Height / scale,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod)
                };
                Canvas.SetLeft(border, -border.Width - frame.ActualWidth);
                host.Children.Add(border);
                return border;
            }
        }

        async Task<WeakReference[]> CyclePreviewAsync(bool save)
        {
            var dialog = await OpenPreviewAsync();
            var references = new WeakReference[]
            {
                new(dialog),
                new(Find<Grid>((FrameworkElement)dialog.Content, grid => grid.Name == "PosterCanvas")),
                new(vm.PreviewCover),
                new(vm.PreviewBackground)
            };
            if (references.Any(static reference => !reference.IsAlive))
                throw new InvalidOperationException("Expected a rendered poster and both image sources before closing.");
            if (save)
            {
                pickedPath = png;
                Invoke(DialogButton("Save PNG"));
                await UntilAsync(() => !vm.IsSaving && vm.SaveStatus.Contains(png, StringComparison.Ordinal));
            }
            Invoke(DialogButton("Close"));
            await UntilAsync(() => page.PreviewDialog is null);
            return references;
        }

        void RecordClosedMemory(string phase, int cycle)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            record($"PREVIEW CLOSED phase={phase} cycle={cycle} pid={process.Id} utcMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} managed={GC.GetTotalMemory(false)} private={process.PrivateMemorySize64}");
        }

        async Task CheckDialogLayoutAsync(string closeLabel)
        {
            var close = DialogButton(closeLabel);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot)
                .Single(item => FindButton(item.Child, closeLabel) == close);
            var surface = Find<Border>(popup.Child, item => item.Name == "BackgroundElement")!;
            var presenter = Find<ContentPresenter>(close)!;
            await Task.Delay(250);
            var root = (UIElement)frame.XamlRoot.Content;
            var bounds = surface.TransformToVisual(root).TransformBounds(new(0, 0, surface.ActualWidth, surface.ActualHeight));
            check(bounds.Top >= 11 && frame.XamlRoot.Size.Height - bounds.Bottom >= 11 &&
                close.ActualHeight > 0 && close.CornerRadius.TopLeft == 4 && presenter.CornerRadius.TopLeft == 4,
                $"dialog at {frame.XamlRoot.Size.Height:F0} DIP has vertical clearance ({bounds.Top:F1}, " +
                $"{frame.XamlRoot.Size.Height - bounds.Bottom:F1} DIP), surface margin {surface.Margin.Top:F0} DIP and rounded close button {presenter.CornerRadius.TopLeft:F0} DIP");
        }

        string fractionalPng = Path.Combine(directory, "fractional-dpi.png");
        using (var rounded = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 1079, 1349, BitmapAlphaMode.Premultiplied))
            await ListeningReportImages.SaveAsync(fractionalPng, rounded, default);
        using (var file = File.OpenRead(fractionalPng))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            check(decoder.PixelWidth == 1080 && decoder.PixelHeight == 1350, "encoding rounds a subpixel capture to the fixed export size");
        }

        Invoke(FindButton((StatsPage)frame.Content, "Listening reports")!);
        await UntilAsync(() => frame.Content is ListeningReportPage && vm.Presentation is not null && !vm.IsLoading);
        page = (ListeningReportPage)frame.Content;
        var pickerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _picker = async (_, token) =>
        {
            pickerStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };
        preview = await OpenPreviewAsync();
        Invoke(DialogButton("Save PNG"));
        await pickerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(!vm.IsSaving && !vm.IsPreview && vm.PreviewCover is null && vm.PreviewBackground is null && vm.Presentation is null,
            "shutdown cancels and drains an in-flight save picker and releases the preview");

        async Task<ListeningReportPreviewDialog> OpenPreviewAsync()
        {
            Invoke(FindButton(page, "Export summary image")!);
            await UntilAsync(() => page.PreviewDialog is { } dialog && dialog.IsPrimaryButtonEnabled &&
                dialog.Content is FrameworkElement { IsLoaded: true } && FindDialogButton("Save PNG") is not null);
            return page.PreviewDialog!;
        }

        Button? FindDialogButton(string label) => VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot)
            .Select(popup => FindButton(popup.Child, label)).FirstOrDefault(button => button is not null);

        Button DialogButton(string label) => FindDialogButton(label)
            ?? throw new InvalidOperationException($"Dialog button '{label}' is missing.");
    }

    private static long SamplePoster(SoftwareBitmap bitmap)
    {
        int bytes = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var buffer = new Windows.Storage.Streams.Buffer((uint)bytes);
        bitmap.CopyToBuffer(buffer);
        using var stream = buffer.AsStream();
        Span<byte> pixel = stackalloc byte[4];
        long signature = 0;
        for (int line = 0; line < bitmap.PixelHeight; line += bitmap.PixelHeight / 7)
        {
            stream.Position = (long)line * bitmap.PixelWidth * 4;
            stream.ReadExactly(pixel);
            if (pixel[3] != 255) throw new InvalidOperationException("Poster background is transparent.");
            signature = unchecked(signature * 397 ^ BitConverter.ToInt32(pixel));
        }
        return signature;
    }

    private static async Task ShaderChecksAsync(Action<bool, string> check)
    {
        using var red = SoftwareBitmap.CreateCopyFromBuffer(new byte[] { 10, 30, 230, 255 }.AsBuffer(),
            BitmapPixelFormat.Bgra8, 1, 1, BitmapAlphaMode.Premultiplied);
        using var blue = SoftwareBitmap.CreateCopyFromBuffer(new byte[] { 230, 30, 10, 255 }.AsBuffer(),
            BitmapPixelFormat.Bgra8, 1, 1, BitmapAlphaMode.Premultiplied);
        foreach (var mode in Enum.GetValues<BackgroundShaderMode>())
        {
            using var first = await Task.Run(() => BackgroundShaderRenderer.RenderCoverStillAsync(red, mode, PaletteAlgorithm.KMeansPP, default));
            using var second = await Task.Run(() => BackgroundShaderRenderer.RenderCoverStillAsync(blue, mode, PaletteAlgorithm.KMeansPP, default));
            check(first.PixelWidth == 432 && first.PixelHeight == 540 && Sample(first) != Sample(second),
                $"{mode} paints a bounded static frame that changes with the cover");
        }

        static long Sample(SoftwareBitmap bitmap)
        {
            int bytes = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
            var buffer = new Windows.Storage.Streams.Buffer((uint)bytes);
            bitmap.CopyToBuffer(buffer);
            using var stream = buffer.AsStream();
            Span<byte> pixel = stackalloc byte[4];
            long signature = 0;
            for (int index = 1; index < 10; index++)
            {
                stream.Position = bytes / 40 * (index * 4);
                stream.ReadExactly(pixel);
                if (pixel[3] != 255 || pixel[0] > 110 || pixel[1] > 110 || pixel[2] > 110)
                    throw new InvalidOperationException("Poster frame is not opaque and dark enough for text contrast.");
                signature = unchecked(signature * 397 ^ BitConverter.ToInt32(pixel));
            }
            return signature;
        }
    }

    private static void QueryChecks(string path, Action<bool, string> check, Action<string> record)
    {
        using var db = new SQLiteConnection(path);
        db.CreateTable<Music>();
        db.CreateTable<PlaybackHistory>();
        db.Execute("CREATE INDEX IX_PlaybackHistory_StartedAt ON PlaybackHistory(StartedAt)");
        var utc = TimeZoneInfo.Utc;
        DateTime end = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime now = new(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime start = end.AddMonths(-1);
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            check(ListeningReportQuery.StartOfDayUtc(new(2024, 3, 11), zone) - ListeningReportQuery.StartOfDayUtc(new(2024, 3, 10), zone) == TimeSpan.FromHours(23) &&
                ListeningReportQuery.StartOfDayUtc(new(2024, 11, 4), zone) - ListeningReportQuery.StartOfDayUtc(new(2024, 11, 3), zone) == TimeSpan.FromHours(25),
                "UTC boundaries follow short and long local days");
        }
        var cuba = TimeZoneInfo.FindSystemTimeZoneById("Cuba Standard Time");
        check(ListeningReportQuery.StartOfDayUtc(new(2024, 3, 10), cuba) == new DateTime(2024, 3, 10, 5, 0, 0, DateTimeKind.Utc) &&
            ListeningReportQuery.StartOfDayUtc(new(2024, 11, 3), cuba) == new DateTime(2024, 11, 3, 4, 0, 0, DateTimeKind.Utc),
            "skipped and repeated local midnights resolve deterministically");

        db.RunInTransaction(() =>
        {
            db.Insert(new Music { Id = 12, SongId = 4 });
            Add(1, 12, "Old title", "Band", start, "First album");
            Add(4, 99, "Latest title", "Band", end.AddTicks(-1), "First album");
            Add(0, 0, "Old library track", "", end.AddDays(-1));
            Add(0, 0, "Old library track", " ", end.AddDays(-1));
            Add(1, 12, "Outside period", "Band", end);
            Add(0, 22, "Future", "Band", now.AddMinutes(1));
        });
        ListeningReport month = ListeningReportQuery.Read(path, new(new(2024, 2), now, utc), default);
        check(month is { PlayCount: 4, ActiveDays: 2, Milliseconds: 480000 } && month.Counts.Length == 29 &&
            month.Counts[0] == 1 && month.Counts[^1] == 3 && month.Songs[0] is { Title: "Latest title", PlayCount: 2 } &&
            month.Artists is [{ Name: "Band", PlayCount: 2 }], "leap month and exact endpoints preserve counts and existing song identity");
        check(month.Habits is { DistinctSongs: 3, LongestStreak: 1 } && month.Habits.WeekdayCounts[(int)DayOfWeek.Thursday] == 4 &&
            month.Habits.HourCounts[0] == 3 && month.Habits.HourCounts[23] == 1 &&
            month.TopSongHistory == new ReportSongHistory(new(2024, 2, 1), 2) &&
            month.Albums is [{ Name: "First album", PlayCount: 2 }] && month.MonthlyArtists.Length == 0,
            "habit metrics, album rank and top track dates respect track versions and local period boundaries");
        var year = ListeningReportQuery.Read(path, new(new(2024), now, utc), default);
        check(year.PlayCount == 5 && year.Counts[1] + year.Counts[2] == 5 && year.InProgress &&
            year.FirstRecord == new DateOnly(2024, 2, 1) && year.LatestRecord == new DateOnly(2024, 3, 1),
            "natural year reconciles with months and excludes future records");
        check(year.MonthlyArtists is [{ Month: 2, Name: "Band", Milliseconds: 240000 }, { Month: 3, Name: "Band", Milliseconds: 120000 }],
            "annual artists are scoped to populated months");
        var empty = ListeningReportQuery.Read(path, new(new(2024, 1), now, utc), default);
        check(empty.PlayCount == 0 && empty.Songs.Length == 0 && empty.LatestRecord == new DateOnly(2024, 3, 1),
            "empty intervals keep the most recent retained date for navigation");

        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Add(0, 0, "Legacy winner", "Early band", new(2024, 11, 3, 5, 15, 0, DateTimeKind.Utc), "Legacy album", 60000);
        Add(0, 0, "Legacy winner", "Early band", new(2024, 11, 3, 6, 45, 0, DateTimeKind.Utc), "Legacy album", 60000);
        Add(0, 0, "Legacy winner", "Late band", new(2024, 11, 4, 6, 10, 0, DateTimeKind.Utc), "Other album", 180000);
        DateTime nextYear = new(2025, 1, 1, 7, 0, 0, DateTimeKind.Utc);
        var repeatedHour = ListeningReportQuery.Read(path, new(new(2024, 11), nextYear, eastern), default);
        check(repeatedHour.Habits is { DistinctSongs: 2, LongestStreak: 2 } && repeatedHour.Habits.HourCounts[1] == 3 &&
            repeatedHour.TopSongHistory == new ReportSongHistory(new(2024, 11, 3), 1),
            "fall-back hours share the same local bucket and legacy labels remain isolated in the top-track history");
        var artistByDuration = ListeningReportQuery.Read(path, new(new(2024), nextYear, eastern), default);
        check(artistByDuration.MonthlyArtists[^1] is { Month: 11, Name: "Late band", Milliseconds: 180000 },
            "the monthly artist lead uses listening time before play count");

        const int size = 240000;
        db.Execute("""
            WITH RECURSIVE fixture(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM fixture WHERE n<?)
            INSERT INTO PlaybackHistory (MusicId,SongIdAtPlay,Title,Author,Album,TotalDurationMs,DurationPlayedMs,StartedAt,EndedAt)
            SELECT n+100,0,'Track '||n,'Artist '||n,'Album',200000,150000,?+(n%91)*864000000000+n,?+(n%91)*864000000000+n+1500000000
            FROM fixture
            """, size, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        var request = new ListeningReportRequest(new(2025), new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
        var stress = ListeningReportQuery.Read(path, request, default);
        check(stress.PlayCount == size && stress.Habits.DistinctSongs == size &&
            stress.Songs.Length == 5 && stress.Artists.Length == 5 && stress.Counts.Length == 12,
            "large library returns fixed-size aggregates and rankings");
        double[] elapsed = new double[8];
        long allocated = 0;
        for (int i = 0; i < elapsed.Length; i++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _ = ListeningReportQuery.Read(path, request, default);
            elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = Math.Max(allocated, GC.GetAllocatedBytesForCurrentThread() - bytes);
        }
        Array.Sort(elapsed);
        record($"240,000 distinct tracks and artists, 8 warm reads: median {(elapsed[3] + elapsed[4]) / 2:F1} ms, max {elapsed[^1]:F1} ms, max thread allocation {allocated:N0} B.");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        try
        {
            _ = ListeningReportQuery.Read(path, request, cancelled.Token);
            throw new InvalidOperationException("The sizeable range query ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
            check(true, "in-progress aggregate responds to cancellation");
        }

        void Add(int song, int music, string title, string artist, DateTime at, string album = "", double playedMs = 120000)
            => db.Insert(new PlaybackHistory
            {
                SongIdAtPlay = song, MusicId = music, Title = title, Author = artist, Album = album, StartedAt = at,
                EndedAt = at.AddMinutes(4), DurationPlayedMs = playedMs, TotalDurationMs = 200000
            });
    }

    private static Button? FindButton(DependencyObject root, string content) =>
        Find<Button>(root, button => Equals(button.Content, content));

    private static T? Find<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T match && (predicate is null || predicate(match))) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is { } result) return result;
        return null;
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task UntilAsync(Func<bool> predicate)
    {
        long stop = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < stop)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Waiting for the report UI.");
    }

}
