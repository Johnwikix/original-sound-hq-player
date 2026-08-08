using Microsoft.Extensions.Logging;
using SQLite;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 播放统计系统核心服务：
    /// 1. 记录每次播放会话（开始时间 / 歌曲元数据快照 / 实际收听时长）；
    /// 2. 参考 scrobble 语义，仅把收听时长达到歌曲总时长一定比例的会话写入历史；
    /// 3. 提供时间段内的汇总查询（总时长、曲目数、Top 歌曲 / 歌手 / 专辑、时段分布）。
    /// 历史表只存入达标会话：未达标会话在结算时直接丢弃，不占用存储。
    /// </summary>
    public class PlaybackStatsService
    {
        /// <summary>一次播放至少听过歌曲时长的该比例，才计入统计。</summary>
        public const double QualifiedRatio = 0.5;

        /// <summary>会话结算写入数据库完成后触发（任意线程）。</summary>
        public event Action? StatsUpdated;

        private readonly AppViewModel _appViewModel;
        private readonly MusicDatabaseService _musicDatabaseService;
        private readonly ILogger<PlaybackStatsService> _logger;
        private readonly Lock _sessionLock = new();

        private PlaybackHistory? _currentSession;

        /// <summary>当前会话上次心跳的播放位置（毫秒），用于按位置差分累计收听时长。</summary>
        private long _lastPositionMs = -1;

        private long _lastErrorLogTicks;

        private long _lastRebaseLogTicks;

        public PlaybackStatsService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<PlaybackStatsService> logger)
        {
            _appViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            _appViewModel.CurrentPlayingTimeChanged += OnCurrentPlayingTimeChanged;
        }

        private SQLiteAsyncConnection Db => _musicDatabaseService.GetDbConnection();

        /// <summary>当前正在收听的会话（可能为 null）。</summary>
        public PlaybackHistory? CurrentSession
        {
            get { lock (_sessionLock) return _currentSession; }
        }

        /// <summary>是否达到统计入库阈值（收听时长超过歌曲总时长比例）。</summary>
        public bool IsQualified(PlaybackHistory session)
        {
            return session.TotalDurationMs > 0
                && session.DurationPlayedMs >= session.TotalDurationMs * QualifiedRatio;
        }

        /// <summary>
        /// 结算上一会话并开始一次新的播放会话。必须在 UI 线程调用（播放入口处）。
        /// 会话不在开始时入库，而是在 <see cref="FlushSession"/> 结算且达标后一次性写入。
        /// 同一首歌在短时间内重复进入（自动切歌与手动点击并发等）会被忽略，避免会话被误切短。
        /// </summary>
        public void StartSession(Music music)
        {
            if (music is null) return;

            lock (_sessionLock)
            {
                var existing = _currentSession;
                if (existing is not null
                    && existing.MusicId == music.Id
                    && (DateTime.UtcNow - existing.StartedAt).TotalSeconds < 5)
                {
                    return;
                }
            }

            FlushSession();

            var session = new PlaybackHistory
            {
                MusicId = music.Id,
                Title = music.Title,
                Author = music.Author,
                Album = music.Album,
                TotalDurationMs = music.Duration.TotalMilliseconds,
                StartedAt = DateTime.UtcNow,
            };
            // FlushSession 会同步清空 _currentSession 后才返回，此处赋值无竞态。
            lock (_sessionLock)
            {
                _currentSession = session;
                _lastPositionMs = -1;
            }
        }

        /// <summary>结算当前会话（达标则写入数据库并触发更新，未达标丢弃）。幂等，可重复调用。</summary>
        public void FlushSession()
        {
            _ = FlushSessionAsync();
        }

        /// <summary>结算当前会话并等待入池完成（退出应用时使用，避免最后会话丢库）。</summary>
        public async Task FlushSessionAsync()
        {
            PlaybackHistory? session;
            lock (_sessionLock)
            {
                session = _currentSession;
                _currentSession = null;
                _lastPositionMs = -1;
            }
            if (session is null) return;

            session.EndedAt = DateTime.UtcNow;
            await PersistSessionAsync(session);
            try
            {
                StatsUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知统计更新失败: {Message}", ex.Message);
            }
        }

        private void OnCurrentPlayingTimeChanged(long currentMs)
        {
            try
            {
                if (!_appViewModel.IsPlaying) return;

                lock (_sessionLock)
                {
                    var session = _currentSession;
                    if (session is null || session.TotalDurationMs <= 0) return;

                    if (_lastPositionMs < 0)
                    {
                        _lastPositionMs = currentMs;
                        return;
                    }

                    long delta = currentMs - _lastPositionMs;
                    _lastPositionMs = currentMs;

                    // 暂停 / 回跳（seek 后退、切歌过渡）只切基准不累计；
                    // 正向跳变（快进、拖动、UI 卡顿后的追赶）按收听时间计入，防止整段丢时。
                    if (delta <= 0)
                    {
                        if (delta < 0 && (DateTime.UtcNow - session.StartedAt).TotalSeconds >= 2)
                        {
                            long now = Environment.TickCount64;
                            if (now - _lastRebaseLogTicks >= 10_000)
                            {
                                _lastRebaseLogTicks = now;
                                _logger.LogInformation("统计位置回跳切基准: MusicId={MusicId}, delta={Delta}ms, pos={Pos}ms",
                                    session.MusicId, delta, currentMs);
                            }
                        }
                        return;
                    }

                    double played = session.DurationPlayedMs + delta;
                    session.DurationPlayedMs = Math.Min(played, session.TotalDurationMs);
                }
            }
            catch (Exception ex)
            {
                // 125ms 心跳路径：统计失败绝不影响播放器与歌词控件的事件链，限频记录。
                long now = Environment.TickCount64;
                if (now - _lastErrorLogTicks >= 10_000)
                {
                    _lastErrorLogTicks = now;
                    _logger.LogError(ex, "统计心跳处理失败: {Message}", ex.Message);
                }
            }
        }

        private async Task PersistSessionAsync(PlaybackHistory session)
        {
            try
            {
                if (!IsQualified(session))
                {
                    _logger.LogInformation("播放会话未达标不入库: MusicId={MusicId}, 收听={PlayedMs:0}ms, 总时长={TotalMs:0}ms, 会话={SessionSec:0}s",
                        session.MusicId, session.DurationPlayedMs, session.TotalDurationMs,
                        (session.EndedAt - session.StartedAt).TotalSeconds);
                    return;
                }

                _logger.LogInformation("播放会话达标入库: MusicId={MusicId}, 收听={PlayedMs:0}ms, 总时长={TotalMs:0}ms",
                    session.MusicId, session.DurationPlayedMs, session.TotalDurationMs);
                await Db.InsertAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录播放会话失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 统计页数据快照：SQL 聚合一次生成总时长、曲目数、Top 歌曲 / 歌手 / 专辑与时段分布。
        /// 所有聚合下推数据库，避免把历史表整表物化到内存。
        /// 汇总 / 活跃天数 / 小时分布 / 每日分布由同一次按 (本地日期, 小时) 分组扫描聚合得到，
        /// 避免对同一范围做多遍重复扫描。
        /// </summary>
        public async Task<StatsSnapshot> GetStatsSnapshotAsync(
            DateTime startUtc, DateTime endUtc, int songLimit = 10, int artistLimit = 5, int albumLimit = 5)
        {
            long startTicks = startUtc.ToUniversalTime().Ticks;
            long endTicks = endUtc.ToUniversalTime().Ticks;

            var snapshot = new StatsSnapshot();

            var rows = await Db.QueryAsync<SqlDayHourRow>(
                "SELECT " +
                "strftime('%Y-%m-%d', datetime((StartedAt / 10000000) - 62135596800, 'unixepoch', 'localtime')) AS Day, " +
                "CAST(strftime('%H', datetime((StartedAt / 10000000) - 62135596800, 'unixepoch', 'localtime')) AS INTEGER) AS Hour, " +
                "COUNT(*) AS Cnt, " +
                "SUM(MIN(DurationPlayedMs, TotalDurationMs)) AS Ms " +
                "FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? " +
                "GROUP BY Day, Hour",
                startTicks, endTicks);

            int tracks = 0;
            double ms = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                tracks += r.Cnt;
                ms += r.Ms;
                if (r.Hour is >= 0 and < 24) snapshot.HourlyCounts[r.Hour] += r.Cnt;
                if (DateTime.TryParseExact(r.Day, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day))
                {
                    if (snapshot.DailyCounts.TryGetValue(day, out var existing))
                    {
                        snapshot.DailyCounts[day] = existing + r.Cnt;
                    }
                    else
                    {
                        snapshot.DailyCounts[day] = r.Cnt;
                    }
                }
            }
            snapshot.TracksPlayedCount = tracks;
            snapshot.ActiveDaysCount = snapshot.DailyCounts.Count;
            snapshot.TotalListeningSeconds = ms / 1000.0;

            snapshot.TopSongs = await BuildTopSongsAsync(startTicks, endTicks, songLimit);
            snapshot.TopArtists = await BuildTopArtistsAsync(startTicks, endTicks, artistLimit);
            snapshot.TopAlbums = await BuildTopAlbumsAsync(startTicks, endTicks, albumLimit);

            return snapshot;
        }

        /// <summary>
        /// 按本地日期聚合会话数（热度图用，可独立于统计时间范围查询）。
        /// </summary>
        public async Task<Dictionary<DateTime, int>> GetDailyCountsAsync(DateTime startUtc, DateTime endUtc)
        {
            long startTicks = startUtc.ToUniversalTime().Ticks;
            long endTicks = endUtc.ToUniversalTime().Ticks;

            var result = new Dictionary<DateTime, int>();
            var days = await Db.QueryAsync<SqlDayRow>(
                "SELECT strftime('%Y-%m-%d', datetime((StartedAt / 10000000) - 62135596800, 'unixepoch', 'localtime')) AS Day, " +
                "COUNT(*) AS Cnt FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? GROUP BY Day",
                startTicks, endTicks);
            for (int i = 0; i < days.Count; i++)
            {
                var d = days[i];
                if (DateTime.TryParseExact(d.Day, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day))
                {
                    result[day] = d.Cnt;
                }
            }
            return result;
        }

        private async Task<List<SongPlayStat>> BuildTopSongsAsync(long startTicks, long endTicks, int topLimit)
        {
            var rows = await Db.QueryAsync<SqlSongRow>(
                "SELECT COALESCE(Title, '') AS Title, COALESCE(Author, '') AS Artist, COALESCE(MAX(Album), '') AS Album, " +
                "MAX(MusicId) AS MusicId, COUNT(*) AS PlayCount, SUM(MIN(DurationPlayedMs, TotalDurationMs)) AS Ms " +
                "FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? " +
                "GROUP BY Title, Artist ORDER BY PlayCount DESC, Ms DESC LIMIT ?",
                startTicks, endTicks, topLimit);

            var result = new List<SongPlayStat>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                result.Add(new SongPlayStat
                {
                    Title = r.Title,
                    Artist = r.Artist,
                    Album = r.Album,
                    MusicId = r.MusicId,
                    PlayCount = r.PlayCount,
                    TotalDurationSeconds = r.Ms / 1000.0,
                });
            }
            return result;
        }

        private async Task<List<ArtistPlayStat>> BuildTopArtistsAsync(long startTicks, long endTicks, int topLimit)
        {
            var rows = await Db.QueryAsync<SqlArtistRow>(
                "SELECT COALESCE(Author, '') AS Artist, MAX(MusicId) AS MusicId, COUNT(*) AS PlayCount, " +
                "SUM(MIN(DurationPlayedMs, TotalDurationMs)) AS Ms " +
                "FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? " +
                "GROUP BY Artist ORDER BY PlayCount DESC, Ms DESC LIMIT ?",
                startTicks, endTicks, topLimit);

            var result = new List<ArtistPlayStat>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                result.Add(new ArtistPlayStat
                {
                    Artist = r.Artist,
                    MusicId = r.MusicId,
                    PlayCount = r.PlayCount,
                    TotalDurationSeconds = r.Ms / 1000.0,
                });
            }
            return result;
        }

        private async Task<List<AlbumPlayStat>> BuildTopAlbumsAsync(long startTicks, long endTicks, int topLimit)
        {
            var rows = await Db.QueryAsync<SqlAlbumRow>(
                "SELECT COALESCE(Album, '') AS Album, MAX(MusicId) AS MusicId, COUNT(*) AS PlayCount, " +
                "SUM(MIN(DurationPlayedMs, TotalDurationMs)) AS Ms " +
                "FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? " +
                "GROUP BY Album ORDER BY PlayCount DESC, Ms DESC LIMIT ?",
                startTicks, endTicks, topLimit);

            var result = new List<AlbumPlayStat>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                result.Add(new AlbumPlayStat
                {
                    Album = r.Album,
                    MusicId = r.MusicId,
                    PlayCount = r.PlayCount,
                    TotalDurationSeconds = r.Ms / 1000.0,
                });
            }
            return result;
        }

        private sealed class SqlDayHourRow
        {
            public string Day { get; set; } = string.Empty;
            public int Hour { get; set; }
            public int Cnt { get; set; }
            public double Ms { get; set; }
        }

        private sealed class SqlDayRow
        {
            public string Day { get; set; } = string.Empty;
            public int Cnt { get; set; }
        }

        private sealed class SqlSongRow
        {
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string Album { get; set; } = string.Empty;
            public int MusicId { get; set; }
            public int PlayCount { get; set; }
            public double Ms { get; set; }
        }

        private sealed class SqlArtistRow
        {
            public string Artist { get; set; } = string.Empty;
            public int MusicId { get; set; }
            public int PlayCount { get; set; }
            public double Ms { get; set; }
        }

        private sealed class SqlAlbumRow
        {
            public string Album { get; set; } = string.Empty;
            public int MusicId { get; set; }
            public int PlayCount { get; set; }
            public double Ms { get; set; }
        }
    }
}