using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.ViewModel
{
    /// <summary>统计时间段（滚动口径，含今天）。</summary>
    public enum StatsRange
    {
        PastWeek = 0,
        PastMonth = 1,
        PastQuarter = 2,
        PastYear = 3,
        Custom = 4,
    }

    /// <summary>
    /// 播放统计页 ViewModel：负责时间范围筛选、数据加载与 Top 列表绑定。
    /// </summary>
    public partial class StatsViewModel : ObservableObject
    {
        private readonly AppViewModel _appViewModel;
        private readonly PlaybackStatsService _statsService;
        private readonly ILogger<StatsViewModel> _logger;
        private DispatcherQueueTimer? _debounceTimer;

        public StatsViewModel(
            AppViewModel appViewModel,
            PlaybackStatsService statsService,
            ILogger<StatsViewModel> logger)
        {
            _appViewModel = appViewModel;
            _statsService = statsService;
            _logger = logger;
            _statsService.StatsUpdated += OnStatsUpdated;
        }

        // ───────────────────────── 时间范围 ─────────────────────────

        public int SelectedTimeRangeIndex
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    IsCustomRangeSelected = value == (int)StatsRange.Custom;
                    DebouncedLoad();
                }
            }
        } = (int)StatsRange.PastWeek;

        public bool IsCustomRangeSelected { get => field; set => SetProperty(ref field, value); }
        public DateTimeOffset? CustomStartDate { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public DateTimeOffset? CustomEndDate { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public TimeSpan CustomStartTime { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public TimeSpan CustomEndTime { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }

        // ───────────────────────── 汇总数据 ─────────────────────────

        public bool IsLoading { get => field; set => SetProperty(ref field, value); }
        public string TotalDurationText { get => field; set => SetProperty(ref field, value); } = "--";
        public int TracksPlayedCount { get => field; set => SetProperty(ref field, value); }

        /// <summary>时间段内活跃收听的天数。</summary>
        public int ActiveDaysCount { get => field; set => SetProperty(ref field, value); }

        public string PeakHourText { get => field; set => SetProperty(ref field, value); } = "--:--";
        public string QuietHourText { get => field; set => SetProperty(ref field, value); } = "--:--";

        /// <summary>热度图节点（按 7 列周布局，行首补空格子对齐星期）。</summary>
        public ObservableCollection<HeatmapNode> HeatmapData { get => field; set => SetProperty(ref field, value); } = [];

        /// <summary>热度图顶部月份标签（Offset 为所在列像素偏移）。</summary>
        public ObservableCollection<MonthLabel> MonthLabels { get => field; set => SetProperty(ref field, value); } = [];

        /// <summary>24 小时活跃度柱状图数据。</summary>
        public ObservableCollection<HourlyActivityItem> HourlySeriesValues { get => field; set => SetProperty(ref field, value); } = [];

        public ObservableCollection<SongPlayStat> TopSongs { get; } = [];
        public ObservableCollection<ArtistPlayStat> TopArtists { get; } = [];
        public ObservableCollection<AlbumPlayStat> TopAlbums { get; } = [];

        /// <summary>Top 10 歌曲前 5 首（分段视图，与歌手/专辑列逐行对齐）。</summary>
        public ObservableCollection<SongPlayStat> TopSongsFirst { get; } = [];

        /// <summary>Top 10 歌曲第 6-10 首（分段视图，与歌手/专辑列逐行对齐）。</summary>
        public ObservableCollection<SongPlayStat> TopSongsSecond { get; } = [];

        // ── 页面生命周期 ─────────────────────────────────────────

        public void OnPageActive()
        {
            EnsureTimers();
            DebouncedLoad();
        }

        public void OnPageInactive()
        {
        }

        private void EnsureTimers()
        {
            if (_debounceTimer is not null) return;
            var dq = App.MainWindow?.DispatcherQueue;
            if (dq is null) return;

            _debounceTimer = dq.CreateTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(500);
            _debounceTimer.IsRepeating = false;
            _debounceTimer.Tick += (s, e) => _ = LoadDataCoreAsync();
        }

        private void OnStatsUpdated()
        {
            var dq = App.MainWindow?.DispatcherQueue;
            if (dq is null) return;
            _ = dq.TryEnqueue(DispatcherQueuePriority.Normal, DebouncedLoad);
        }

        private void DebouncedLoad()
        {
            if (_debounceTimer is null)
            {
                _ = LoadDataCoreAsync();
                return;
            }
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private (DateTime startUtc, DateTime endUtc) CalculateRange()
        {
            DateTime now = DateTime.Now;

            if ((StatsRange)SelectedTimeRangeIndex == StatsRange.Custom)
            {
                DateTime customStart = Combine(CustomStartDate?.LocalDateTime ?? now.Date, CustomStartTime);
                DateTime customEnd = Combine(CustomEndDate?.LocalDateTime ?? now, CustomEndTime);
                if (customEnd < customStart)
                {
                    (customStart, customEnd) = (customEnd, customStart);
                }
                return (customStart.ToUniversalTime(), customEnd.ToUniversalTime());
            }

            // 滚动口径：过去 N 天（含今天）。
            DateTime startLocal = SelectedTimeRangeIndex switch
            {
                (int)StatsRange.PastMonth => now.Date.AddDays(-29),
                (int)StatsRange.PastQuarter => now.Date.AddDays(-89),
                (int)StatsRange.PastYear => now.Date.AddDays(-364),
                _ => now.Date.AddDays(-6),
            };

            DateTime endLocal = now.Date.AddDays(1).AddTicks(-1);

            return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
        }

        private static DateTime Combine(DateTime date, TimeSpan time)
            => new(date.Year, date.Month, date.Day, time.Hours, time.Minutes, time.Seconds, DateTimeKind.Local);

        private async Task LoadDataCoreAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var (startUtc, endUtc) = CalculateRange();

                var snapshot = await _statsService.GetStatsSnapshotAsync(startUtc, endUtc);

                TotalDurationText = FormatHours(snapshot.TotalListeningSeconds);
                TracksPlayedCount = snapshot.TracksPlayedCount;
                ActiveDaysCount = snapshot.ActiveDaysCount;

                ApplyTopSongs(snapshot.TopSongs);
                ApplyTopArtists(snapshot.TopArtists);
                ApplyTopAlbums(snapshot.TopAlbums);

                UpdateHourlyPeaks(snapshot.HourlyCounts);
                UpdateHourlySeries(snapshot.HourlyCounts);

                // 热度图固定展示滚动过去一年，不随所选时间范围变化。
                // 选「过去一年」时热度图范围与快照范围一致，直接复用快照日聚合，不再单独扫描。
                DateTime now = DateTime.Now;
                var heatmapStartLocal = now.Date.AddDays(-364);
                var heatmapEndLocal = now.Date.AddDays(1).AddTicks(-1);

                var dailyCounts = SelectedTimeRangeIndex == (int)StatsRange.PastYear
                    ? snapshot.DailyCounts
                    : await _statsService.GetDailyCountsAsync(heatmapStartLocal, heatmapEndLocal);
                ApplyHeatmap(dailyCounts, heatmapStartLocal, heatmapEndLocal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载播放统计失败: {Message}", ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 增量应用 Top 歌曲：相同歌曲仅更新数值属性（行与封面不重建，避免闪烁）；
        /// 仅对榜单变化项做替换/增删。
        /// </summary>
        private void ApplyTopSongs(List<SongPlayStat> fresh)
        {
            int oldCount = TopSongs.Count;
            int min = Math.Min(oldCount, fresh.Count);
            for (int i = 0; i < min; i++)
            {
                var old = TopSongs[i];
                var next = fresh[i];
                if (old.Title == next.Title && old.Artist == next.Artist && old.MusicId == next.MusicId)
                {
                    ResolveMusic(old);
                    old.PlayCount = next.PlayCount;
                    old.TotalDurationSeconds = next.TotalDurationSeconds;
                    continue;
                }
                ResolveMusic(next);
                TopSongs[i] = next;
            }
            for (int i = oldCount; i < fresh.Count; i++)
            {
                ResolveMusic(fresh[i]);
                TopSongs.Add(fresh[i]);
            }
            for (int i = fresh.Count; i < oldCount; i++)
            {
                TopSongs.RemoveAt(fresh.Count);
            }
            SyncTopSongSegments();
        }

        /// <summary>把 Top 10 歌曲同步为两段（1-5 / 6-10），与左侧歌手/专辑列逐行对齐。</summary>
        private void SyncTopSongSegments()
        {
            SyncSegment(TopSongsFirst, TopSongs, 0, Math.Min(TopSongs.Count, 5));
            SyncSegment(TopSongsSecond, TopSongs, 5, Math.Max(0, Math.Min(TopSongs.Count, 10) - 5));
        }

        /// <summary>增量同步分段视图：复用同一对象引用，避免行重建闪烁。</summary>
        private static void SyncSegment(ObservableCollection<SongPlayStat> dst, IReadOnlyList<SongPlayStat> src, int start, int len)
        {
            int cur = dst.Count;
            for (int i = 0; i < len; i++)
            {
                var item = src[start + i];
                if (i < cur)
                {
                    if (!ReferenceEquals(dst[i], item))
                    {
                        dst[i] = item;
                    }
                }
                else
                {
                    dst.Add(item);
                }
            }
            for (int i = len; i < cur; i++)
            {
                dst.RemoveAt(len);
            }
        }

        /// <summary>
        /// 增量应用 Top 歌手：相同歌手仅更新数值属性，避免行重建闪烁。
        /// </summary>
        private void ApplyTopArtists(List<ArtistPlayStat> fresh)
        {
            int oldCount = TopArtists.Count;
            int min = Math.Min(oldCount, fresh.Count);
            for (int i = 0; i < min; i++)
            {
                var old = TopArtists[i];
                var next = fresh[i];
                if (old.Artist == next.Artist && old.MusicId == next.MusicId)
                {
                    ResolveMusic(old);
                    old.PlayCount = next.PlayCount;
                    old.TotalDurationSeconds = next.TotalDurationSeconds;
                    continue;
                }
                ResolveMusic(next);
                TopArtists[i] = next;
            }
            for (int i = oldCount; i < fresh.Count; i++)
            {
                ResolveMusic(fresh[i]);
                TopArtists.Add(fresh[i]);
            }
            for (int i = fresh.Count; i < oldCount; i++)
            {
                TopArtists.RemoveAt(fresh.Count);
            }
        }

        private void ApplyTopAlbums(List<AlbumPlayStat> fresh)
        {
            int oldCount = TopAlbums.Count;
            int min = Math.Min(oldCount, fresh.Count);
            for (int i = 0; i < min; i++)
            {
                var old = TopAlbums[i];
                var next = fresh[i];
                if (old.Album == next.Album && old.MusicId == next.MusicId)
                {
                    ResolveMusic(old);
                    old.PlayCount = next.PlayCount;
                    old.TotalDurationSeconds = next.TotalDurationSeconds;
                    continue;
                }
                ResolveMusic(next);
                TopAlbums[i] = next;
            }
            for (int i = oldCount; i < fresh.Count; i++)
            {
                ResolveMusic(fresh[i]);
                TopAlbums.Add(fresh[i]);
            }
            for (int i = fresh.Count; i < oldCount; i++)
            {
                TopAlbums.RemoveAt(fresh.Count);
            }
        }

        private void ResolveMusic(SongPlayStat stat)
        {
            if (_appViewModel.TryFindById(stat.MusicId, out var m) && m is not null)
            {
                stat.Music = m;
            }
        }

        private void ResolveMusic(ArtistPlayStat stat)
        {
            if (_appViewModel.TryFindById(stat.MusicId, out var m) && m is not null)
            {
                stat.Music = m;
            }
        }

        private void ResolveMusic(AlbumPlayStat stat)
        {
            if (_appViewModel.TryFindById(stat.MusicId, out var m) && m is not null)
            {
                stat.Music = m;
            }
        }

        private static string FormatHours(double totalSeconds)
        {
            return (totalSeconds / 3600.0).ToString("F2");
        }

        private void UpdateHourlyPeaks(int[] counts)
        {
            int maxIdx = 0, minIdx = 0, maxVal = -1, minVal = int.MaxValue;
            bool any = false;
            for (int i = 0; i < 24; i++)
            {
                if (counts[i] > 0) any = true;
                if (counts[i] > maxVal) { maxVal = counts[i]; maxIdx = i; }
                if (counts[i] < minVal) { minVal = counts[i]; minIdx = i; }
            }
            if (!any)
            {
                PeakHourText = "--:--";
                QuietHourText = "--:--";
                return;
            }
            PeakHourText = $"{maxIdx:D2}:00 - {maxIdx + 1:D2}:00";
            QuietHourText = $"{minIdx:D2}:00 - {minIdx + 1:D2}:00";
        }

        /// <summary>
        /// 构建 GitHub 风格日活跃热度图：按周一/文化首日起始列，7 列一周；
        /// 播放天数 ≥ 最大值 25% 分四档强度；月份变化处生成顶部标签。
        /// 传入本地日期范围（含起止日）。
        /// </summary>
        private void ApplyHeatmap(Dictionary<DateTime, int> dailyCounts, DateTime startLocal, DateTime endLocal)
        {
            if (dailyCounts.Count == 0)
            {
                HeatmapData = [];
                MonthLabels = [];
                return;
            }

            var culture = CultureInfo.CurrentUICulture;
            var startDate = startLocal.Date;
            var endDate = endLocal.Date;

            var maxCount = 0;
            foreach (var value in dailyCounts.Values)
            {
                if (value > maxCount) maxCount = value;
            }

            var nodes = new List<HeatmapNode>();
            var monthLabels = new List<MonthLabel>();

            var startDayOfWeek = (int)culture.DateTimeFormat.FirstDayOfWeek;
            for (var i = 0; i < startDayOfWeek; i++) nodes.Add(new HeatmapNode { IsEmpty = true });

            var currentMonth = startDate.Month;
            var currentYear = startDate.Year;

            if (DateTime.DaysInMonth(startDate.Year, startDate.Month) - startDate.Day >= 15)
            {
                monthLabels.Add(new MonthLabel
                {
                    Name = startDate.ToString("MMM", culture),
                    Offset = 0
                });
            }

            var days = (int)(endDate - startDate).TotalDays + 1;

            for (var i = 0; i < days; i++)
            {
                var currentDate = startDate.AddDays(i);

                if (currentDate.Month != currentMonth)
                {
                    currentMonth = currentDate.Month;

                    var colIndex = nodes.Count / 7;
                    double offset = colIndex * 18 + 2;

                    string labelName;
                    if (currentDate.Year != currentYear)
                    {
                        currentYear = currentDate.Year;
                        labelName = currentDate.ToString("y", culture);
                    }
                    else
                    {
                        labelName = currentDate.ToString("MMM", culture);
                    }

                    monthLabels.Add(new MonthLabel
                    {
                        Name = labelName,
                        Offset = offset
                    });
                }

                dailyCounts.TryGetValue(currentDate, out var count);
                var level = 0;
                if (count > 0)
                {
                    if (maxCount <= 4)
                    {
                        level = count;
                    }
                    else
                    {
                        var ratio = (double)count / maxCount;
                        if (ratio <= 0.25) level = 1;
                        else if (ratio <= 0.5) level = 2;
                        else if (ratio <= 0.75) level = 3;
                        else level = 4;
                    }
                }

                nodes.Add(new HeatmapNode
                {
                    Date = currentDate,
                    PlayCount = count,
                    Level = level,
                    IsEmpty = false,
                    TooltipDate = currentDate.ToShortDateString()
                });
            }

            HeatmapData = new ObservableCollection<HeatmapNode>(nodes);
            MonthLabels = new ObservableCollection<MonthLabel>(monthLabels);
        }

        /// <summary>由小时计数生成 24 根柱状图数据（高度按峰值归一化）。</summary>
        private void UpdateHourlySeries(int[] counts)
        {
            var maxHourCount = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > maxHourCount) maxHourCount = counts[i];
            }

            var items = new List<HourlyActivityItem>(24);
            for (int i = 0; i < 24; i++)
            {
                items.Add(new HourlyActivityItem
                {
                    TimeLabel = $"{i:D2}:00",
                    Count = counts[i],
                    HeightPercentage = maxHourCount == 0 ? 0 : (double)counts[i] / maxHourCount,
                    TooltipText = counts[i].ToString()
                });
            }

            HourlySeriesValues = new ObservableCollection<HourlyActivityItem>(items);
        }
    }
}