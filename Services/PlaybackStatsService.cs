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
    /// 2. 参考 scrobble 语义，仅把收听时长达到歌曲总时长一定比例的会话计入统计；
    /// 3. 提供时间段内的汇总查询（总时长、曲目数、Top 歌曲 / 歌手 / 专辑、时段分布）。
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
        private int _lastSecond = -1;
        private long _lastErrorLogTicks;

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

        public bool IsQualified(PlaybackHistory session)
        {
            return session.TotalDurationMs > 0
                && session.DurationPlayedMs >= session.TotalDurationMs * QualifiedRatio;
        }

        /// <summary>
        /// 结算上一会话并开始一次新的播放会话。必须在 UI 线程调用（播放入口处）。
        /// </summary>
        public void StartSession(Music music)
        {
            if (music is null) return;
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
            lock (_sessionLock)
            {
                _currentSession = session;
                _lastSecond = -1;
            }

            // 先入库拿到 Id；切歌 / 停止 / 退出时 UpdateAsync 结算收听时长。
            _ = PersistInsertAsync(session);
        }

        /// <summary>结算当前会话（写入结束时间与收听时长）。幂等，可重复调用。</summary>
        public void FlushSession()
        {
            PlaybackHistory? session;
            lock (_sessionLock)
            {
                session = _currentSession;
                _currentSession = null;
                _lastSecond = -1;
            }
            if (session is null) return;
            session.EndedAt = DateTime.UtcNow;
            _ = FinalizeAndNotifyAsync(session);
        }

        private async Task FinalizeAndNotifyAsync(PlaybackHistory session)
        {
            await PersistUpdateAsync(session);
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

                PlaybackHistory session;
                lock (_sessionLock)
                {
                    session = _currentSession;
                    if (session is null) return;
                    int second = (int)(currentMs / 1000);
                    if (second == _lastSecond) return;
                    _lastSecond = second;
                }

                double played = session.DurationPlayedMs + 1000;
                session.DurationPlayedMs = session.TotalDurationMs > 0
                    ? Math.Min(played, session.TotalDurationMs)
                    : played;
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

        private async Task PersistInsertAsync(PlaybackHistory session)
        {
            try
            {
                await Db.InsertAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录播放会话失败: {Message}", ex.Message);
            }
        }

        private async Task PersistUpdateAsync(PlaybackHistory session)
        {
            try
            {
                await Db.UpdateAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "结算播放历史会话失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 取时间段内所有达标（听满阈值比例）的会话。
        /// 统计页请使用 <see cref="GetStatsSnapshotAsync"/> 一次性聚合，避免重复全表查询。
        /// </summary>
        public async Task<List<PlaybackHistory>> GetQualifiedLogsAsync(DateTime startUtc, DateTime endUtc)
        {
            var rows = await Db.Table<PlaybackHistory>()
                .Where(h => h.StartedAt >= startUtc && h.StartedAt <= endUtc)
                .ToListAsync();
            if (rows.Count == 0) return rows;

            var qualified = new List<PlaybackHistory>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsQualified(rows[i])) qualified.Add(rows[i]);
            }
            return qualified;
        }

        /// <summary>
        /// 统计页数据快照：一次查询 + 内存聚合，生成总时长、曲目数、Top 歌曲 / 歌手 / 专辑与时段分布。
        /// </summary>
        public async Task<StatsSnapshot> GetStatsSnapshotAsync(DateTime startUtc, DateTime endUtc, int topLimit = 10)
        {
            var rows = await GetQualifiedLogsAsync(startUtc, endUtc);
            var snapshot = new StatsSnapshot
            {
                TracksPlayedCount = rows.Count,
                HourlyCounts = new int[24],
            };
            if (rows.Count == 0) return snapshot;

            var songs = new Dictionary<(string Title, string Author), (int Count, double Ms, string Album)>();
            var artists = new Dictionary<string, (int Count, double Ms)>();
            var albums = new Dictionary<string, (int Count, double Ms)>();

            double totalMs = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string title = row.Title ?? string.Empty;
                string author = row.Author ?? string.Empty;
                string album = row.Album ?? string.Empty;
                double ms = Math.Min(row.DurationPlayedMs, row.TotalDurationMs);
                totalMs += ms;
                snapshot.HourlyCounts[row.StartedAt.ToLocalTime().Hour]++;

                var songKey = (title, author);
                if (!songs.TryGetValue(songKey, out var song))
                {
                    song = (0, 0, album);
                    songs[songKey] = song;
                }
                song = (song.Count + 1, song.Ms + ms, string.IsNullOrEmpty(song.Album) ? album : song.Album);
                songs[songKey] = song;

                if (!artists.TryGetValue(author, out var artist))
                {
                    artist = (0, 0);
                    artists[author] = artist;
                }
                artist = (artist.Count + 1, artist.Ms + ms);
                artists[author] = artist;

                if (!albums.TryGetValue(album, out var albumSlot))
                {
                    albumSlot = (0, 0);
                    albums[album] = albumSlot;
                }
                albumSlot = (albumSlot.Count + 1, albumSlot.Ms + ms);
                albums[album] = albumSlot;
            }

            snapshot.TotalListeningSeconds = totalMs / 1000.0;

            snapshot.TopSongs = BuildTopSongs(songs, topLimit);
            snapshot.TopArtists = BuildTopArtists(artists, topLimit);
            BuildTopAlbum(snapshot, albums);

            return snapshot;
        }

        private static List<SongPlayStat> BuildTopSongs(Dictionary<(string Title, string Author), (int Count, double Ms, string Album)> songs, int limit)
        {
            var result = new List<SongPlayStat>(songs.Count);
            foreach (var kv in songs)
            {
                result.Add(new SongPlayStat
                {
                    Title = kv.Key.Title,
                    Artist = kv.Key.Author,
                    Album = kv.Value.Album,
                    PlayCount = kv.Value.Count,
                    TotalDurationSeconds = kv.Value.Ms / 1000.0,
                });
            }
            result.Sort(static (a, b) => b.PlayCount != a.PlayCount
                ? b.PlayCount.CompareTo(a.PlayCount)
                : b.TotalDurationSeconds.CompareTo(a.TotalDurationSeconds));
            if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
            return result;
        }

        private static List<ArtistPlayStat> BuildTopArtists(Dictionary<string, (int Count, double Ms)> artists, int limit)
        {
            var result = new List<ArtistPlayStat>(artists.Count);
            foreach (var kv in artists)
            {
                result.Add(new ArtistPlayStat
                {
                    Artist = kv.Key,
                    PlayCount = kv.Value.Count,
                    TotalDurationSeconds = kv.Value.Ms / 1000.0,
                });
            }
            result.Sort(static (a, b) => b.PlayCount != a.PlayCount
                ? b.PlayCount.CompareTo(a.PlayCount)
                : b.TotalDurationSeconds.CompareTo(a.TotalDurationSeconds));
            if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
            return result;
        }

        private static void BuildTopAlbum(StatsSnapshot snapshot, Dictionary<string, (int Count, double Ms)> albums)
        {
            string bestName = string.Empty;
            int bestCount = 0;
            double bestMs = 0;
            foreach (var kv in albums)
            {
                if (kv.Value.Count > bestCount || (kv.Value.Count == bestCount && kv.Value.Ms > bestMs))
                {
                    bestCount = kv.Value.Count;
                    bestMs = kv.Value.Ms;
                    bestName = kv.Key;
                }
            }
            snapshot.TopAlbumName = bestName;
            snapshot.TopAlbumPlayCount = bestCount;
        }
    }
}