using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SQLite;
using Windows.Graphics.Imaging;
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
    internal static async Task RunAsync(Frame frame, SQLiteAsyncConnection db, ListeningReportViewModel vm,
        string directory, string cover, Action<bool, string> check, Action<string> record)
    {
        directory = Path.Combine(directory, "listening-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await Task.Run(() => QueryChecks(Path.Combine(directory, "reports.db"), check, record));

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
            VALUES (1234,11,'Older retained track','','',180000,180000,?,?)
            """, new DateTime(previousYear, 1, 2, 12, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(previousYear, 1, 2, 12, 3, 0, DateTimeKind.Utc).Ticks);
        string path = WebDavCachePaths.FindCover(AppSettings.MusicCoverCache, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(cover, path, overwrite: true);

        var statistics = (StatsPage)frame.Content;
        var entry = FindButton(statistics, "Listening reports") ?? throw new InvalidOperationException("Localised report entry missing.");
        Invoke(entry);
        await UntilAsync(() => frame.Content is ListeningReportPage && vm.Presentation is not null && !vm.IsLoading);
        var page = (ListeningReportPage)frame.Content;
        check(vm.ModeIndex == 0 && vm.Presentation!.Tiles.Length == 12, "statistics entry opens the compiled annual report");
        var radio = Find<RadioButtons>(page) ?? throw new InvalidOperationException("Period control missing.");
        radio.SelectedIndex = 1;
        await UntilAsync(() => !vm.IsLoading && vm.Presentation?.Weekdays.Length == 7);
        check(vm.ModeIndex == 1 && vm.Presentation!.PlayCount == "2", "compiled natural-month selection updates counts");
        vm.SelectedYear = previousYear;
        vm.SelectedMonth = 1;
        vm.SelectedMonth = 12;
        await UntilAsync(() => !vm.IsLoading && vm.SelectedMonth == 12 && vm.StatusText.Length > 0);
        check(vm.Presentation is null && !vm.OpenPreviewCommand.CanExecute(null) && vm.OpenRecentCommand.CanExecute(null),
            "fast period switching shows the latest empty period and offers recovery");
        Invoke(FindButton(page, "View latest period with records")!);
        await UntilAsync(() => !vm.IsLoading && vm.Presentation?.PlayCount == "2");

        // Substitute only the path picker. The page's real capture delegate and PNG writer are exercised.
        string png = Path.Combine(directory, "listening-report.png");
        int pickerCalls = 0;
        string? pickedPath = null;
        string? suggested = null;
        vm.Deactivate();
        vm.Activate(page.CaptureAsync, (name, token) => { pickerCalls++; suggested = name; return Task.FromResult(pickedPath); });
        await UntilAsync(() => !vm.IsLoading && vm.Presentation is not null);
        var previewButton = FindButton(page, "Export summary image") ?? throw new InvalidOperationException("Localised preview button missing.");
        Invoke(previewButton);
        await UntilAsync(() => vm.IsPreview && !vm.IsPreparingPreview);
        check(vm.PreviewCover is not null && vm.Presentation!.PosterSongs.Contains("Poster fixture track", StringComparison.Ordinal),
            "preview uses rendered report text and cached bounded cover");

        var saveButton = FindButton(page, "Save PNG") ?? throw new InvalidOperationException("Localised save button missing.");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 1 && !vm.IsSaving);
        check(vm.IsPreview && !File.Exists(png) && vm.SaveStatus.Length == 0, "picker cancellation returns to the same preview");

        pickedPath = Path.Combine(directory, "absent", "report.png");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 2 && !vm.IsSaving);
        check(vm.SaveStatus.Contains("Couldn’t save", StringComparison.Ordinal), "PNG write failure reports an error and leaves retry available");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBefore = process.PrivateMemorySize64;
        long managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        double rasterization = page.XamlRoot.RasterizationScale;
        pickedPath = png;
        File.WriteAllText(png, "previous file");
        Invoke(saveButton);
        await UntilAsync(() => pickerCalls == 3 && !vm.IsSaving);
        watch.Stop();
        process.Refresh();
        record($"PNG save at {rasterization:P0} scale: {watch.Elapsed.TotalMilliseconds:F0} ms, managed process allocation delta " +
            $"{GC.GetTotalAllocatedBytes(precise: true) - managedBefore:N0} B, private bytes change at completion {process.PrivateMemorySize64 - privateBefore:N0} B.");
        if (!vm.SaveStatus.Contains(png, StringComparison.Ordinal))
        {
            using var diagnostic = await page.CaptureAsync(default);
            await ListeningReportImages.SaveAsync(png, diagnostic, default);
            throw new InvalidOperationException("Save command failed despite direct capture and encoding: " + vm.SaveStatus);
        }
        check(File.Exists(png) &&
            suggested == "OriginalSound-Listening-" + DateTime.Now.Year && !Directory.GetFiles(directory, "*.part").Any(),
            "summary PNG replaces the existing file and cleans temporary output");
        using (var file = File.OpenRead(png))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            check(decoder.PixelWidth == 1080 && decoder.PixelHeight == 1350, "real PNG decodes to 1080 by 1350");
            var pixelData = await decoder.GetPixelDataAsync();
            byte[] pixels = pixelData.DetachPixelData();
            int bottom = (1350 * 1080 - 1) * 4;
            check(pixels[3] == 255 && pixels[bottom + 3] == 255 && pixels[0] < 80 &&
                !pixels.AsSpan(0, 4).SequenceEqual(pixels.AsSpan(bottom, 4)), "PNG captures the full opaque gradient poster");
        }

        Invoke(FindButton(page, "Back to report")!);
        await UntilAsync(() => !vm.IsPreview && vm.PreviewCover is null);
        page.NavigateBack();
        await UntilAsync(() => frame.Content is StatsPage);
        check(vm.Presentation is null, "leaving the report releases its presentation and artwork");

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
        vm.Deactivate();
        var pickerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.Activate(page.CaptureAsync, async (_, token) =>
        {
            pickerStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        await UntilAsync(() => !vm.IsLoading && vm.Presentation is not null);
        Invoke(FindButton(page, "Export summary image")!);
        await UntilAsync(() => vm.IsPreview && !vm.IsPreparingPreview);
        Invoke(FindButton(page, "Save PNG")!);
        await pickerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(!vm.IsSaving && !vm.IsPreview && vm.PreviewCover is null && vm.Presentation is null,
            "shutdown cancels and drains an in-flight save picker and releases the preview");
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
            Add(1, 12, "Old title", "Band", start);
            Add(4, 99, "Latest title", "Band", end.AddTicks(-1));
            Add(0, 0, "Old library track", "", end.AddDays(-1));
            Add(0, 0, "Old library track", " ", end.AddDays(-1));
            Add(1, 12, "Outside period", "Band", end);
            Add(0, 22, "Future", "Band", now.AddMinutes(1));
        });
        ListeningReport month = ListeningReportQuery.Read(path, new(new(2024, 2), now, utc), default);
        check(month is { PlayCount: 4, ActiveDays: 2, Milliseconds: 480000 } && month.Counts.Length == 29 &&
            month.Counts[0] == 1 && month.Counts[^1] == 3 && month.Songs[0] is { Title: "Latest title", PlayCount: 2 } &&
            month.Artists is [{ Name: "Band", PlayCount: 2 }], "leap month and exact endpoints preserve counts and existing song identity");
        var year = ListeningReportQuery.Read(path, new(new(2024), now, utc), default);
        check(year.PlayCount == 5 && year.Counts[1] + year.Counts[2] == 5 && year.InProgress &&
            year.FirstRecord == new DateOnly(2024, 2, 1) && year.LatestRecord == new DateOnly(2024, 3, 1),
            "natural year reconciles with months and excludes future records");
        var empty = ListeningReportQuery.Read(path, new(new(2024, 1), now, utc), default);
        check(empty.PlayCount == 0 && empty.Songs.Length == 0 && empty.LatestRecord == new DateOnly(2024, 3, 1),
            "empty intervals keep the most recent retained date for navigation");

        const int size = 240000;
        db.Execute("""
            WITH RECURSIVE fixture(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM fixture WHERE n<?)
            INSERT INTO PlaybackHistory (MusicId,SongIdAtPlay,Title,Author,Album,TotalDurationMs,DurationPlayedMs,StartedAt,EndedAt)
            SELECT n+100,0,'Track '||n,'Artist '||n,'Album',200000,150000,?+(n%91)*864000000000+n,?+(n%91)*864000000000+n+1500000000
            FROM fixture
            """, size, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        var request = new ListeningReportRequest(new(2025), new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), utc);
        var stress = ListeningReportQuery.Read(path, request, default);
        check(stress.PlayCount == size && stress.Songs.Length == 5 && stress.Artists.Length == 5 && stress.Counts.Length == 12,
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

        void Add(int song, int music, string title, string artist, DateTime at)
            => db.Insert(new PlaybackHistory
            {
                SongIdAtPlay = song, MusicId = music, Title = title, Author = artist, StartedAt = at,
                EndedAt = at.AddMinutes(4), DurationPlayedMs = 120000, TotalDurationMs = 200000
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
