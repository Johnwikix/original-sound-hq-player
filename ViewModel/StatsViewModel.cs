using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.ViewModel
{
    /// <summary>统计时间段。</summary>
    public enum StatsRange
    {
        Today = 0,
        ThisWeek = 1,
        ThisMonth = 2,
        ThisQuarter = 3,
        ThisYear = 4,
        AllTime = 5,
        Custom = 6,
    }

    /// <summary>
    /// 播放统计页 ViewModel：负责时间范围筛选、数据加载与 Top 列表绑定。
    /// </summary>
    public partial class StatsViewModel : ObservableObject
    {
        private static readonly DateTime AllTimeStartUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly AppViewModel _appViewModel;
        private readonly MusicBrowseViewModel _musicBrowseViewModel;
        private readonly PlaybackStatsService _statsService;
        private readonly ILogger<StatsViewModel> _logger;
        private DispatcherQueueTimer? _debounceTimer;

        public StatsViewModel(
            AppViewModel appViewModel,
            MusicBrowseViewModel musicBrowseViewModel,
            PlaybackStatsService statsService,
            ILogger<StatsViewModel> logger)
        {
            _appViewModel = appViewModel;
            _musicBrowseViewModel = musicBrowseViewModel;
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
        } = (int)StatsRange.Today;

        public bool IsCustomRangeSelected { get => field; set => SetProperty(ref field, value); }
        public DateTimeOffset? CustomStartDate { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public DateTimeOffset? CustomEndDate { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public TimeSpan CustomStartTime { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }
        public TimeSpan CustomEndTime { get => field; set { if (SetProperty(ref field, value) && IsCustomRangeSelected) DebouncedLoad(); } }

        // ───────────────────────── 汇总数据 ─────────────────────────

        public bool IsLoading { get => field; set => SetProperty(ref field, value); }
        public string TotalDurationText { get => field; set => SetProperty(ref field, value); } = "--";
        public int TracksPlayedCount { get => field; set => SetProperty(ref field, value); }
        public string TopAlbumText { get => field; set => SetProperty(ref field, value); } = "--";

        /// <summary>最常播放专辑的歌曲对象（该专辑歌曲全删后为 null）。</summary>
        public Music? TopAlbumMusic { get => field; set => SetProperty(ref field, value); }
        public string PeakHourText { get => field; set => SetProperty(ref field, value); } = "--:--";
        public string QuietHourText { get => field; set => SetProperty(ref field, value); } = "--:--";

        public ObservableCollection<SongPlayStat> TopSongs { get; } = [];
        public ObservableCollection<ArtistPlayStat> TopArtists { get; } = [];

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

        [RelayCommand]
        private void Refresh() => DebouncedLoad();

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

            DateTime startLocal = now.Date;
            switch ((StatsRange)SelectedTimeRangeIndex)
            {
                case StatsRange.ThisWeek:
                    int dayOfWeek = (int)now.DayOfWeek;
                    if (dayOfWeek == 0) dayOfWeek = 7;
                    startLocal = now.Date.AddDays(-(dayOfWeek - 1));
                    break;
                case StatsRange.ThisMonth:
                    startLocal = new DateTime(now.Year, now.Month, 1);
                    break;
                case StatsRange.ThisQuarter:
                    startLocal = new DateTime(now.Year, (now.Month - 1) / 3 * 3 + 1, 1);
                    break;
                case StatsRange.ThisYear:
                    startLocal = new DateTime(now.Year, 1, 1);
                    break;
                case StatsRange.AllTime:
                    startLocal = AllTimeStartUtc.ToLocalTime().Date;
                    break;
            }

            DateTime endLocal = SelectedTimeRangeIndex switch
            {
                (int)StatsRange.ThisWeek => startLocal.AddDays(7),
                (int)StatsRange.ThisMonth => startLocal.AddMonths(1),
                (int)StatsRange.ThisQuarter => startLocal.AddMonths(3),
                (int)StatsRange.ThisYear => startLocal.AddYears(1),
                (int)StatsRange.AllTime => now.Date.AddDays(1),
                _ => startLocal.AddDays(1),
            };
            endLocal = endLocal.AddTicks(-1);

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
                _logger.LogInformation("加载播放统计，时间范围: {Start:O} ~ {End:O}", startUtc, endUtc);

                var snapshot = await _statsService.GetStatsSnapshotAsync(startUtc, endUtc, 10);

                TotalDurationText = FormatHours(snapshot.TotalListeningSeconds);
                TracksPlayedCount = snapshot.TracksPlayedCount;

                ApplyTopSongs(snapshot.TopSongs);
                ApplyTopArtists(snapshot.TopArtists);

                TopAlbumText = string.IsNullOrEmpty(snapshot.TopAlbumName) ? "--" : snapshot.TopAlbumName;
                TopAlbumMusic = _appViewModel.TryFindById(snapshot.TopAlbumMusicId, out var albumMusic) ? albumMusic : null;

                UpdateHourlyPeaks(snapshot.HourlyCounts);
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

        [RelayCommand]
        private async Task PlayTopAlbum()
        {
            var albumMusic = TopAlbumMusic;
            if (albumMusic is null) return;

            var src = _appViewModel.SongsSource;
            var list = new List<Music>(Math.Max(src.Count, 1));
            for (int i = 0; i < src.Count; i++)
            {
                var m = src[i];
                if (m.Album is not null && m.Album.Equals(albumMusic.Album, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(m);
                }
            }
            if (list.Count == 0) return;

            list.Sort((a, b) => string.CompareOrdinal(a.Album, b.Album));
            _appViewModel.SequentialPlayingList = new BulkObservableCollection<Music>(list);
            await _musicBrowseViewModel.PlayMusic(list[0], IsChangeList: true);
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
    }
}