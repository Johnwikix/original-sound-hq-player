using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService
    {
        private SQLiteAsyncConnection _dbConnection;
        private string DbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        private string SettingsPath => GetSettingsFilePath();
        private string PlayStatePath => GetPlayStateFilePath();
        private string VersionRecordPath => GetVersionRecordFilePath();
        private readonly AddFolderService addFolderService = new();
        private SaveSettings _currentSettings;
        public SaveSettings CurrentSettings => _currentSettings;
        private SavePlayState _currentPlayState;
        public SavePlayState CurrentPlayState => _currentPlayState;
        // 优化1: 信号量保持4并发，但 _toDelete/_toUpdate 改为方法局部变量，消除共享状态与线程安全隐患
        private readonly SemaphoreSlim _rescanfolderSemaphore = new(4, 4);
        private AppViewModel AppViewModel { get; set; }
        private ILogger<MusicDatabaseService> _logger;

        public MusicDatabaseService(ILogger<MusicDatabaseService> logger)
        {
            _logger = logger;
        }

        public async Task Initialize()
        {
            InitalizeDbPath();
            if (_dbConnection is null)
            {
                _dbConnection = new SQLiteAsyncConnection(DbPath);
                await _dbConnection.CreateTableAsync<Music>();
                await _dbConnection.CreateTableAsync<MusicLyrics>();
                await _dbConnection.CreateTableAsync<Folder>();
                await _dbConnection.CreateTableAsync<SaveEqualizer>();
                await _dbConnection.CreateTableAsync<PlayList>();
                await _dbConnection.CreateTableAsync<PlayListMusic>();
                await _dbConnection.CreateTableAsync<LastPlayListState>();
                await _dbConnection.CreateTableAsync<SubFolder>();
                await _dbConnection.CreateTableAsync<UsbDeviceMusic>();
                try
                {
                    await _dbConnection.CreateTableAsync<PlaybackHistory>();
                    await _dbConnection.ExecuteAsync(
                        "CREATE INDEX IF NOT EXISTS IX_PlaybackHistory_StartedAt ON PlaybackHistory(StartedAt)");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "初始化播放统计表失败，统计功能降级不可用: {Message}", ex.Message);
                }
            }
            AppViewModel = App.Services.GetRequiredService<AppViewModel>();
            await MigrateLyricsAsync();
        }

        private void InitalizeDbPath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (userProfilePath is not null)
                {
                    string appFolderPath = Path.Combine(userProfilePath, "OriginalSoundPlayer", "DataBase");
                    string dbFilePath = Path.Combine(appFolderPath, "MusicDatabase.db");
                    string sourceDbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
                    if (!Directory.Exists(appFolderPath))
                    {
                        Directory.CreateDirectory(appFolderPath);
                        CopyFile(sourceDbPath, dbFilePath);
                        DbPath = dbFilePath;
                    }
                    else
                    {
                        if (!File.Exists(dbFilePath))
                        {
                            CopyFile(sourceDbPath, dbFilePath);
                        }
                        DbPath = dbFilePath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"InitalizeDbPath 初始化数据库路径失败: {ex.Message}");
                DbPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            }
        }

        private string GetSettingsFilePath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string appFolderPath = Path.Combine(userProfilePath, "OriginalSoundPlayer", "Settings");
                if (!Directory.Exists(appFolderPath))
                {
                    Directory.CreateDirectory(appFolderPath);
                }
                return Path.Combine(appFolderPath, "Settings.json");
            }
            catch
            {
                return Path.Combine(ApplicationData.Current.LocalFolder.Path, "Settings.json");
            }
        }

        private string GetPlayStateFilePath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string appFolderPath = Path.Combine(userProfilePath, "OriginalSoundPlayer", "Settings");
                if (!Directory.Exists(appFolderPath))
                {
                    Directory.CreateDirectory(appFolderPath);
                }
                return Path.Combine(appFolderPath, "PlayState.json");
            }
            catch
            {
                return Path.Combine(ApplicationData.Current.LocalFolder.Path, "PlayState.json");
            }
        }

        private string GetDesktopLyricsStateFilePath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string appFolderPath = Path.Combine(userProfilePath, "OriginalSoundPlayer", "Settings");
                if (!Directory.Exists(appFolderPath))
                {
                    Directory.CreateDirectory(appFolderPath);
                }
                return Path.Combine(appFolderPath, "DesktopLyricsState.json");
            }
            catch
            {
                return Path.Combine(ApplicationData.Current.LocalFolder.Path, "DesktopLyricsState.json");
            }
        }

        public SaveDesktopLyricsState LoadDesktopLyricsState()
        {
            try
            {
                string path = GetDesktopLyricsStateFilePath();
                if (!File.Exists(path))
                {
                    return new SaveDesktopLyricsState();
                }
                return JsonSerializer.Deserialize(File.ReadAllText(path), DesktopLyricsStateJsonContext.Default.SaveDesktopLyricsState) ?? new SaveDesktopLyricsState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LoadDesktopLyricsState 读取桌面歌词窗口状态失败: {ex.Message}");
                return new SaveDesktopLyricsState();
            }
        }

        public void SaveDesktopLyricsState(SaveDesktopLyricsState state)
        {
            try
            {
                string path = GetDesktopLyricsStateFilePath();
                File.WriteAllText(path, JsonSerializer.Serialize(state, DesktopLyricsStateJsonContext.Default.SaveDesktopLyricsState));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveDesktopLyricsState 写入桌面歌词窗口状态失败: {ex.Message}");
            }
        }

        private string GetVersionRecordFilePath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string appFolderPath = Path.Combine(userProfilePath, "OriginalSoundPlayer", "Settings");
                if (!Directory.Exists(appFolderPath))
                {
                    Directory.CreateDirectory(appFolderPath);
                }
                return Path.Combine(appFolderPath, "VersionRecord.json");
            }
            catch
            {
                return Path.Combine(ApplicationData.Current.LocalFolder.Path, "VersionRecord.json");
            }
        }

        private void CopyFile(string sourceFilePath, string targetFilePath)
        {
            if (File.Exists(sourceFilePath))
            {
                using FileStream sourceStream = File.Open(sourceFilePath, FileMode.Open);
                using FileStream destinationStream = File.Create(targetFilePath);
                sourceStream.CopyTo(destinationStream);
            }
        }

        public SQLiteAsyncConnection GetDbConnection()
        {
            return _dbConnection;
        }

        public async Task SavePlayList(List<Music> currentPlayingList)
        {
            await _dbConnection.DeleteAllAsync<LastPlayListState>();
            // 优化2: 去掉多余的 ToArray()，string.Join 直接接受 IEnumerable<int>
            // 注意: ZLinq.ValueEnumerable 是 struct，未实现 IEnumerable<T>，
            //       .AsEnumerable() 会分配 enumerator，所以保留 .ToArray()。
            var musicIds = string.Join(',', currentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
            var playListState = new LastPlayListState
            {
                PlayListMusicIds = musicIds
            };
            await _dbConnection.InsertAsync(playListState);
        }

        public async Task InsertSubFolders(List<SubFolder> subFolder)
        {
            await _dbConnection.InsertAllAsync(subFolder);
        }

        public async Task AddSubFolder(SubFolder subFolder)
        {
            await _dbConnection.InsertAsync(subFolder);
        }

        public async Task UpdateSubFolder(SubFolder subFolder)
        {
            await _dbConnection.UpdateAsync(subFolder);
        }

        public async Task DeleteSubFolder(SubFolder subFolder)
        {
            await _dbConnection.DeleteAsync(subFolder);
        }

        public async Task DeleteSubFolderByPath(string subFolderPath)
        {
            var musicToDelete = await _dbConnection.Table<Music>()
                                              .Where(m => m.Path.Contains(subFolderPath))
                                              .ToListAsync();
            foreach (var music in musicToDelete)
            {
                await _dbConnection.DeleteAsync(music);
            }
        }

        public async Task DeleteAllSubFolder()
        {
            await _dbConnection.DeleteAllAsync<SubFolder>();
        }

        public async Task<List<SubFolder>> GetSubFolders(int folderId)
        {
            return await _dbConnection.Table<SubFolder>().Where(f => f.FolderId == folderId).ToListAsync();
        }


        public async Task<List<Folder>> GetFolders()
        {
            return await _dbConnection.Table<Folder>().ToListAsync();
        }

        public async Task<List<Music>> LoadPlayList(IEnumerable<Music> AllMusicList)
        {
            var playListState = await _dbConnection.Table<LastPlayListState>().FirstOrDefaultAsync();
            if (playListState is null)
            {
                return [];
            }
            var musicIds = ParseCsvIntList(playListState.PlayListMusicIds);
            var musicList = new List<Music>(musicIds.Count);
            foreach (var musicId in musicIds)
            {
                var music = AllMusicList.FirstOrDefault(m => m.Id == musicId);
                if (music is not null)
                {
                    musicList.Add(music);
                }
            }
            return musicList;
        }

        private static List<int> ParseCsvIntList(string csv)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(csv)) return result;
            ReadOnlySpan<char> span = csv;
            while (span.Length > 0)
            {
                int comma = span.IndexOf(',');
                ReadOnlySpan<char> segment = comma >= 0 ? span[..comma] : span;
                if (segment.Length > 0 && int.TryParse(segment, out int id))
                    result.Add(id);
                span = comma >= 0 ? span[(comma + 1)..] : [];
            }
            return result;
        }

        public async Task<List<Folder>> GetFoldersAsync()
        {
            try
            {
                return await _dbConnection.Table<Folder>().ToListAsync();
            }
            catch (SQLiteException)
            {
                return new List<Folder>();
            }
        }

        public async Task InitalPlayListAsync()
        {
            try
            {
                var list = await _dbConnection.Table<PlayList>().ToListAsync();
                await AppViewModel.AllPlayList.AddRangeAsync(list);
            }
            catch (Exception ex) { _logger.LogError(ex, $"InitalPlayListAsync 初始化播放列表失败: {ex.Message}"); }
        }

        public async Task UpdateMusicInfo(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }

        // 定义接收 pragma_table_info 结果的类
        private class TableColumnInfo
        {
            public string Name { get; set; }
        }

        private async Task MigrateLyricsAsync()
        {
            var settings = await GetSettings();
            if (settings.IsLyricsMigrated)
                return;

            var rowCount = await _dbConnection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM MusicLyrics LIMIT 1");
            if (rowCount > 0)
            {
                settings.IsLyricsMigrated = true;
                await UpdateSettings(settings);
                return;
            }

            try
            {
                var lyricsColumnCount = await _dbConnection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM pragma_table_info('Music') WHERE name IN ('Lyrics', 'TranslatedLyrics', 'Krc', 'TKrc')");

                if (lyricsColumnCount > 0)
                {
                    await _dbConnection.ExecuteAsync(
                        "INSERT INTO MusicLyrics (MusicId, Lyrics, TranslatedLyrics, Krc, TKrc) " +
                        "SELECT Id, Lyrics, TranslatedLyrics, Krc, TKrc FROM Music " +
                        "WHERE Lyrics IS NOT NULL OR TranslatedLyrics IS NOT NULL " +
                        "OR Krc IS NOT NULL OR TKrc IS NOT NULL");
                }
            }
            finally
            {
                settings.IsLyricsMigrated = true;
                await UpdateSettings(settings);
            }
        }

        public async Task<(string? lyrics, string? transLrc, string? krc, string? tKrc)> GetLyricsAsync(int musicId)
        {
            var lyrics = await _dbConnection.FindAsync<MusicLyrics>(musicId);
            return (lyrics?.Lyrics, lyrics?.TranslatedLyrics, lyrics?.Krc, lyrics?.TKrc);
        }

        public async Task SaveLyricsAsync(int musicId, string? lyrics, string? transLrc, string? krc, string? tKrc)
        {
            await _dbConnection.InsertOrReplaceAsync(new MusicLyrics
            {
                MusicId = musicId,
                Lyrics = lyrics ?? "",
                TranslatedLyrics = transLrc ?? "",
                Krc = krc ?? "",
                TKrc = tKrc ?? ""
            });
        }

        private async Task SaveEmbeddedLyricsAsync(IEnumerable<(Music Music, string Lyrics)> results)
        {
            foreach (var (music, lyrics) in results)
            {
                if (string.IsNullOrWhiteSpace(lyrics)) continue;
                var existing = await _dbConnection.FindAsync<MusicLyrics>(music.Id);
                if (existing is not null &&
                    !(string.IsNullOrWhiteSpace(existing.Lyrics) && string.IsNullOrWhiteSpace(existing.TranslatedLyrics) &&
                      string.IsNullOrWhiteSpace(existing.Krc) && string.IsNullOrWhiteSpace(existing.TKrc)))
                {
                    continue;
                }
                await SaveLyricsAsync(music.Id, lyrics, null, null, null);
            }
        }

        public IEnumerable<PlayListMusicItem> GetMusicByPlayListIdFromMem(int playListId, string search = null)
        {
            var plmSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppData.AllPlayListMusics);
            int plmCount = plmSpan.Length;
            bool hasSearch = !string.IsNullOrEmpty(search);

            var pool = System.Buffers.ArrayPool<PlayListMusicItem>.Shared;
            var buf = pool.Rent(plmCount);
            int written = 0;
            try
            {
                for (int i = 0; i < plmCount; i++)
                {
                    ref readonly var plm = ref plmSpan[i];
                    if (plm.PlayListId != playListId) continue;
                    if (!AppViewModel.TryFindById(plm.MusicId, out var m) || m is null) continue;
                    if (hasSearch)
                    {
                        bool match = (m.Title is not null && m.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                                     (m.Album is not null && m.Album.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                                     (m.Author is not null && m.Author.Contains(search, StringComparison.OrdinalIgnoreCase));
                        if (!match) continue;
                    }
                    buf[written++] = new PlayListMusicItem { Music = m, PlayListOrder = plm.Order };
                }

                var slice = buf.AsSpan(0, written);
                slice.Sort(_plmOrderDesc);
                return slice.ToArray();
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }

        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _plmOrderDesc =
            System.Collections.Generic.Comparer<PlayListMusicItem>.Create((a, b) => b.PlayListOrder.CompareTo(a.PlayListOrder));

        public async Task UpdatePlayListMusicOrderBatch(int playListId, IEnumerable<PlayListMusicItem> musicList)
        {
            try
            {
                var items = musicList.AsValueEnumerable().ToArray();
                if (items.Length == 0) return;

                await _dbConnection.RunInTransactionAsync(conn =>
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        var m = items[i];
                        conn.Execute(
                            "UPDATE PlayListMusic SET [Order]=? WHERE PlayListId=? AND MusicId=?",
                            m.PlayListOrder, playListId, m.Music.Id);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdatePlayListMusicOrderBatch 批量更新播放列表音乐排序时出错: {ex.Message}");
            }
        }

        public IEnumerable<Music> FindMusicListByAlbum(string album)
        {
            return AppViewModel.SongsSource.AsValueEnumerable()
                   .Where(m => m.Album is not null && m.Album.ToLower().Equals(album.ToLower())).OrderBy(m => m.TrackNumber).ToImmutableList();
        }

        public async Task AddMusicListToFavour(IEnumerable<Music> musics)
        {
            var maxOrder = await GetMaxOrder();
            foreach (var music in musics)
            {
                var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Id == music.Id && m.IsFavorite == true).FirstOrDefaultAsync();
                if (existingMusic is not null)
                {
                    continue;
                }
                music.IsFavorite = true;
                music.Order = maxOrder + 1;
                await _dbConnection.UpdateAsync(music);
            }
        }

        public async Task AddMusicListToPlayList(IEnumerable<Music> musics, int playListId)
        {
            PlayListMusic lastplayListMusic = await _dbConnection.Table<PlayListMusic>()
                                          .Where(m => m.PlayListId == playListId)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            var maxOrder = lastplayListMusic?.Order ?? 0;

            // 优化3: 批量收集后一次 InsertAllAsync，减少多次 await 往返
            var toInsert = new List<PlayListMusic>();
            foreach (var music in musics)
            {
                var existingRecord = await _dbConnection.Table<PlayListMusic>()
                   .Where(plm => plm.PlayListId == playListId && plm.MusicId == music.Id)
                   .FirstOrDefaultAsync();
                if (existingRecord is not null)
                {
                    continue;
                }
                maxOrder++;
                toInsert.Add(new PlayListMusic
                {
                    PlayListId = playListId,
                    MusicId = music.Id,
                    Order = maxOrder
                });
            }
            if (toInsert.Count > 0)
            {
                await _dbConnection.InsertAllAsync(toInsert);
            }
            AppData.AllPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
            RefreshPlayListSongCount(playListId);
        }

        public async Task AddMusicToPlayList(int playListId, int musicId)
        {
            var existingRecord = await _dbConnection.Table<PlayListMusic>()
               .Where(plm => plm.PlayListId == playListId && plm.MusicId == musicId)
               .FirstOrDefaultAsync();
            if (existingRecord is null)
            {
                PlayListMusic lastplayListMusic = await _dbConnection.Table<PlayListMusic>()
                                          .Where(m => m.PlayListId == playListId)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
                // 优化4: 简化 maxOrder 计算逻辑，去掉多余分支
                int newOrder = (lastplayListMusic?.Order ?? 0) + 1;
                var playListMusic = new PlayListMusic
                {
                    PlayListId = playListId,
                    MusicId = musicId,
                    Order = newOrder
                };
                await _dbConnection.InsertAsync(playListMusic);
            }
            AppData.AllPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }

        private async Task<int> GetMaxOrder()
        {
            Music lastFavouriteMusic = await _dbConnection.Table<Music>()
                                          .Where(m => m.IsFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            // 优化5: 用 null 合并简化，减少分支
            return lastFavouriteMusic?.Order ?? 1;
        }

        public async Task DeleteAllMusicFromPlayList(int playListId, IEnumerable<int> musicIds)
        {
            // 优化6: 用 !musicIds.Any() 代替 AsValueEnumerable().Count() == 0，避免全枚举
            if (musicIds is null || !musicIds.Any())
            {
                return;
            }

            var musicIdsString = string.Join(",", musicIds);
            var sql = $"DELETE FROM PlayListMusic WHERE PlayListId = ? AND MusicId IN ({musicIdsString})";
            await _dbConnection.ExecuteAsync(sql, playListId);
            RefreshPlayListSongCount(playListId);
        }

        public async Task RemoveMusicFromPlayList(int playListId, int musicId)
        {
            var playListMusic = await _dbConnection.Table<PlayListMusic>()
                .Where(plm => plm.PlayListId == playListId && plm.MusicId == musicId)
                .FirstOrDefaultAsync();

            if (playListMusic is not null)
            {
                await _dbConnection.DeleteAsync(playListMusic);
            }
            RefreshPlayListSongCount(playListId);
        }

        public async Task<int> InsertPlayList(PlayList playList)
        {
            await _dbConnection.InsertAsync(playList);
            return playList.Id;
        }

        public async Task UpdatePlayList(PlayList playList)
        {
            await _dbConnection.UpdateAsync(playList);
        }

        public async Task<PlayList> GetPlayListByName(string playListName)
        {
            return await _dbConnection.Table<PlayList>()
                .Where(plm => plm.Name == playListName)
                .FirstOrDefaultAsync();
        }

        public async Task RemovePlayList(PlayList playList)
        {
            var playListMusics = await _dbConnection.Table<PlayListMusic>()
               .Where(plm => plm.PlayListId == playList.Id)
               .ToListAsync();
            foreach (var playListMusic in playListMusics)
            {
                await _dbConnection.DeleteAsync(playListMusic);
            }
            await _dbConnection.DeleteAsync(playList);
        }

        public async Task UpdateAllAsync(IEnumerable<Music> musicList)
        {
            await _dbConnection.UpdateAllAsync(musicList);
        }

        public async Task<SaveSettings> GetSettings()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    var defaultSettings = new SaveSettings();
                    await WriteSettingsToJson(defaultSettings);
                    return defaultSettings;
                }
                string json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SaveSettings)
                       ?? new SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message, ex.StackTrace);
                return new SaveSettings();
            }
        }

        public async Task InsertSettings(SaveSettings settings)
        {
            await WriteSettingsToJson(settings);
        }

        public async Task UpdateSettings(SaveSettings settings)
        {
            await WriteSettingsToJson(settings);
        }

        private async Task WriteSettingsToJson(SaveSettings settings)
        {
            try
            {
                string path = SettingsPath;
                string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.SaveSettings);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WriteSettingsToJson 写入设置文件时出错: {ex.Message}");
            }
        }

        public async Task<SavePlayState> GetPlayState()
        {
            try
            {
                string path = PlayStatePath;
                if (!File.Exists(path))
                {
                    return null;
                }
                string json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize(json, PlayStateJsonContext.Default.SavePlayState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message, ex.StackTrace);
                return null;
            }
        }

        private async Task WritePlayStateToJson(SavePlayState state)
        {
            try
            {
                string path = PlayStatePath;
                string json = JsonSerializer.Serialize(state, PlayStateJsonContext.Default.SavePlayState);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WritePlayStateToJson 写入播放状态文件时出错: {ex.Message}");
            }
        }

        public async Task<SaveEqualizer> GetEqualizer()
        {
            return await _dbConnection.Table<SaveEqualizer>().FirstOrDefaultAsync();
        }

        public async Task InsertEqualizer(SaveEqualizer equalizer)
        {
            await _dbConnection.InsertAsync(equalizer);
        }

        public async Task UpdateEqualizer(SaveEqualizer equalizer)
        {
            await _dbConnection.UpdateAsync(equalizer);
        }

        public async Task UpdateEqualizerSettings(string equalizerStr, bool isEnabled)
        {
            await _dbConnection.ExecuteAsync(
                "UPDATE SaveEqualizer SET EqualizerStr = ?, IsEqualizerEnabled = ? WHERE Id = 1",
                equalizerStr,
                isEnabled
            );
        }

        public async Task GetPlayListMusic()
        {
            AppData.AllPlayListMusics.Clear();
            AppData.AllPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
            RefreshAllPlayListSongCounts();
        }

        private void RefreshAllPlayListSongCounts()
        {
            var appVm = AppViewModel;
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < AppData.AllPlayListMusics.Count; i++)
            {
                var plm = AppData.AllPlayListMusics[i];
                if (!appVm.TryFindById(plm.MusicId, out var m) || m is null) continue;
                if (!counts.ContainsKey(plm.PlayListId)) counts[plm.PlayListId] = 0;
                counts[plm.PlayListId]++;
            }
            for (int i = 0; i < appVm.AllPlayList.Count; i++)
            {
                var pl = appVm.AllPlayList[i];
                pl.SongCount = counts.GetValueOrDefault(pl.Id, 0);
            }
        }

        private void RefreshPlayListSongCount(int playListId)
        {
            var appVm = AppViewModel;
            int count = 0;
            for (int i = 0; i < AppData.AllPlayListMusics.Count; i++)
            {
                var plm = AppData.AllPlayListMusics[i];
                if (plm.PlayListId != playListId) continue;
                if (!appVm.TryFindById(plm.MusicId, out var m) || m is null) continue;
                count++;
            }
            for (int i = 0; i < appVm.AllPlayList.Count; i++)
            {
                if (appVm.AllPlayList[i].Id == playListId)
                {
                    appVm.AllPlayList[i].SongCount = count;
                    return;
                }
            }
        }

        public async Task LoadMusicList()
        {
            AppViewModel.SongsSource.Clear();
            AppViewModel.SongsSource.AddRange(await GetMusicListAsync());
            await InitalPlayListAsync();
            await GetPlayListMusic();
            AppViewModel.SequentialPlayingList = new(await LoadPlayList(AppViewModel.SongsSource));
            AppViewModel.NotifySongsSourceChanged();
        }

        public async Task<IReadOnlyCollection<Music>> GetMusicListAsync()
        {
            string localizedUnknownAlbum = ToolUtils.GetString("UnknownAlbum");
            string localizedUnknownArtist = ToolUtils.GetString("UnknownArtist");
            var musicList = await _dbConnection
                .Table<Music>()
                .OrderBy(m => m.Title)
                .ToListAsync();

            // 优化7: AppData.UnknownAlbums / UnknownArtists 建议在 AppData 中改为 HashSet<string>
            // 以将 Contains 从 O(n) 降为 O(1)，此处调用方式不变，修改点在 AppData 定义处
            foreach (var music in musicList)
            {
                if (AppData.UnknownAlbums.Contains(music.Album) && music.Album != localizedUnknownAlbum)
                {
                    music.Album = localizedUnknownAlbum;
                }

                if (AppData.UnknownArtists.Contains(music.Author) && music.Author != localizedUnknownArtist)
                {
                    music.Author = localizedUnknownArtist;
                }
            }

            return musicList;
        }

        public ObservableCollection<Music> GetFavoriteMusicFromMem(string search = null)
        {
            return new(AppViewModel.SongsSource.Where(m => m.IsFavorite == true).OrderByDescending(m => m.Order));
        }

        public IEnumerable<Music> GetArtistMusicFromMem(string artist, string search = null)
        {
            var query = AppViewModel.SongsSource.AsValueEnumerable();
            if (artist is not null)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    return query.Where(m => ArtistHelper.IsMusicByArtist(m, artist))
                        .Where(m =>
                        m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                        m.Album is not null && m.Album.ToLower().Contains(search.ToLower())
                    ).OrderBy(m => m.Album).ToImmutableList();
                }
                else
                {
                    return query.Where(m => ArtistHelper.IsMusicByArtist(m, artist))
                         .OrderBy(m => m.Album).ToImmutableList();
                }
            }
            return query.OrderBy(m => m.Album).ToImmutableList();
        }

        public IEnumerable<Music> GetFolderMusicFromMem(string folder, string search = null)
        {
            var query = AppViewModel.SongsSource.AsValueEnumerable();
            if (folder is not null)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    return query.Where(m =>
                        m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                        m.Album is not null && m.Album.ToLower().Contains(search.ToLower()) ||
                        m.Author is not null && m.Author.ToLower().Contains(search.ToLower())
                    ).Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower()))
                    .OrderBy(m => m.LastLevelFolderPath).ToImmutableList();
                }
                else
                {
                    return query.Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower()))
                         .OrderBy(m => m.LastLevelFolderPath).ToImmutableList();
                }
            }
            return query.OrderBy(m => m.LastLevelFolderPath).ToImmutableList();
        }

        public async Task GetPlayStateAsync()
        {
            bool isFirstTime = !File.Exists(PlayStatePath);
            var playState = _currentPlayState ?? await GetPlayState();
            playState ??= new SavePlayState();
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (playState.LastPlayedMusicId is null && AppViewModel.SongsSource.Count > 0)
                {
                    playState.LastPlayedMusicId = AppViewModel.SongsSource[0].Id;
                }
                AppViewModel.CurrentPlayMode = playState.PlayMode;
                AppViewModel.PlayModeFlyoutText = ToolUtils.GetPlayModeText(playState.PlayMode);
                AppViewModel.CurrentPlayingMusic = LoadCurrentPlayingMusic(playState.LastPlayedMusicId);
                AppViewModel.Volume = playState.Volume;
                AppViewModel.TempVolume = playState.Volume;
                AppViewModel.SelectedSortOption = AppViewModel.SortOptions.AsValueEnumerable().FirstOrDefault(item => item.Tag == playState.SortOrder)
                    ?? AppViewModel.SortOptions.AsValueEnumerable().FirstOrDefault() ?? new SortOption("DefaultOrder", "SortOrderDefault");
                if (isFirstTime)
                {
                    _ = WritePlayStateToJson(playState);
                }
            });
        }

        public void LoadWindowState()
        {
            try
            {
                if (!File.Exists(PlayStatePath))
                {
                    _currentPlayState = new SavePlayState();
                    return;
                }
                string json = File.ReadAllText(PlayStatePath);
                _currentPlayState = JsonSerializer.Deserialize(json, PlayStateJsonContext.Default.SavePlayState)
                    ?? new SavePlayState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LoadWindowState 读取播放状态文件时出错: {ex.Message}");
                _currentPlayState = new SavePlayState();
            }
        }

        public async Task GetEqualizerSettingsAsync()
        {
            var equalizerSettings = await GetEqualizer();
            if (equalizerSettings is null)
            {
                equalizerSettings = new SaveEqualizer();
                await InsertEqualizer(equalizerSettings);
            }
            if (equalizerSettings is not null)
            {
                AppSettings.IsEqualizerEnabled = equalizerSettings.IsEqualizerEnabled;
                AppSettings.EqualizerStr = equalizerSettings.EqualizerStr;
                AppSettings.Equalizer = ToolUtils.ConvertToDictionary(equalizerSettings.EqualizerStr);
                AppSettings.EqualizerPreset = equalizerSettings.EqualizerPreset;
            }
        }

        public async Task GetSettingsAsync()
        {
            var settings = await GetSettings();
            if (settings is null)
            {
                settings = new SaveSettings
                {
                    MusicCoverCache = Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicCoverCache")
                };
                await InsertSettings(settings);
            }
            _currentSettings = settings;
            if (settings is not null)
            {
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.DeviceName = settings.DeviceFriendlyName;
                AppSettings.BassOutputDeviceId = settings.BassOutputDeviceId;
                AppViewModel.DefaultEntryComboBoxTag = settings.DefaultEntry;
                AppViewModel.DefaultPlayListComboBoxTag = settings.DefaultPlayList;
                AppViewModel.Latency = settings.Latency;
                AppViewModel.BackdropType = settings.AppStyle;
                AppViewModel.ThemeType = settings.AppTheme;
                AppViewModel.IsRunningBackend = settings.IsRunningBackend;
                AppSettings.IsDesktopLyricsEnabled = settings.IsDesktopLyricsEnabled;
                AppSettings.IsDesktopLyricsLocked = settings.IsDesktopLyricsLocked;
                AppSettings.DesktopLyricsFontSize = settings.DesktopLyricsFontSize;
                AppSettings.DesktopLyricsFontFamily = settings.DesktopLyricsFontFamily;
                AppSettings.DesktopLyricsColorRgb = settings.DesktopLyricsColorRgb;
                AppSettings.IsDesktopLyricsOutlineEnabled = settings.IsDesktopLyricsOutlineEnabled;
                AppSettings.DesktopLyricsFontWeight = settings.DesktopLyricsFontWeight;
                AppSettings.DesktopLyricsOutlineWidth = settings.DesktopLyricsOutlineWidth;
                AppViewModel.IsAutoLyricsEnabled = settings.IsAutoLyricsEnabled;
                AppViewModel.IsAutoCoverEnabled = settings.IsAutoCoverEnabled;
                AppViewModel.DsdGain = settings.DsdGain;
                AppViewModel.DsdPcmFreq = settings.DsdPcmFreq;
                AppViewModel.CoverSize = settings.CoverSize;
                AppViewModel.IsFluidBackgroundEnabled = settings.IsFluidBackgroundEnabled;
                AppViewModel.BackgroundShader = (AnimatedWin2dControls.BackgroundShaderMode)settings.BackgroundShader;
                AppViewModel.IsFogEffectEnabled = settings.IsFogEffectEnabled;
                AppViewModel.IsSnowEffectEnabled = settings.IsSnowEffectEnabled;
                AppViewModel.IsRaindropEffectEnabled = settings.IsRaindropEffectEnabled;
                AppViewModel.IsFolderWatchEnabled = settings.IsFolderWatchEnabled;
                AppViewModel.IsCustomAppSize = settings.IsCustomAppSize;
                AppViewModel.AppWidth = settings.AppWidth;
                AppViewModel.AppHeight = settings.AppHeight;
                AppViewModel.FontFamilyList = new ObservableCollection<FontInfo>(ToolUtils.GetSystemFontsInternal());
                AppViewModel.FontFamily = AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(new FontFamily(settings.GlobalFont).Source));
                AppViewModel.DesktopLyricsFontSize = settings.DesktopLyricsFontSize;
                AppViewModel.DesktopLyricsFontFamily = AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(new FontFamily(settings.DesktopLyricsFontFamily).Source))
                    ?? AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault();
                AppViewModel.DesktopLyricsColor = Color.FromArgb(0xFF,
                    (byte)((settings.DesktopLyricsColorRgb >> 16) & 0xFF),
                    (byte)((settings.DesktopLyricsColorRgb >> 8) & 0xFF),
                    (byte)(settings.DesktopLyricsColorRgb & 0xFF));
                AppViewModel.IsDesktopLyricsOutlineEnabled = settings.IsDesktopLyricsOutlineEnabled;
                AppViewModel.CustomOpacity = settings.CustomAcrylicOpacity;
                AppViewModel.CustomColor = Color.FromArgb(
                    (byte)((settings.CustomColorArgb >> 24) & 0xFF),
                    (byte)((settings.CustomColorArgb >> 16) & 0xFF),
                    (byte)((settings.CustomColorArgb >> 8) & 0xFF),
                    (byte)(settings.CustomColorArgb & 0xFF));
                AppViewModel.IsCustomLyricsColorEnabled = settings.IsCustomLyricsColorEnabled;
                AppViewModel.LyricsCustomColor = Color.FromArgb(0xFF,
                    (byte)((settings.LyricsCustomColorRgb >> 16) & 0xFF),
                    (byte)((settings.LyricsCustomColorRgb >> 8) & 0xFF),
                    (byte)(settings.LyricsCustomColorRgb & 0xFF));
                AppViewModel.IsUpdateBackDrop = settings.IsUpdateBackDrop;
                AppViewModel.LyricsAlignment = settings.LyricsAlignment;
                AppViewModel.LyricsMargin = new Thickness(settings.LyricsMargin, 0, settings.LyricsMargin, 0);
                AppViewModel.GlobalFontSize = settings.GlobalFontSize;
                AppViewModel.IsGlobalFontSizeEnabled = settings.IsGlobalFontSizeEnabled;
                AppViewModel.MusicCoverCache = string.IsNullOrEmpty(settings.MusicCoverCache) ? Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicCoverCache") : settings.MusicCoverCache;
                AppViewModel.IsDopEnabled = settings.IsDopEnabled;
                AppViewModel.IsFadeEnabled = settings.IsFadeEnabled;
                AppViewModel.LyricsBlurAmount = settings.LyricsBlurAmount;
                AppViewModel.UseImageDominantTheme = settings.UseImageDominantTheme;
                AppViewModel.EnableLightWave = settings.EnableLightWave;
                AppViewModel.PaletteAlgorithm = (AnimatedWin2dControls.Impressionist.PaletteAlgorithm)settings.PaletteAlgorithm;
                AppViewModel.IsWin2dCoverImageControlEnable = settings.IsWin2dCoverImageControlEnable;
                AppViewModel.IsWin2dAnimatedText = settings.IsWin2dAnimatedText;
                AppViewModel.Win2dTextEffectType = AppViewModel.TextEffectItems.AsValueEnumerable().FirstOrDefault(t => t.Value == settings.Win2dTextEffectType) ?? AppViewModel.TextEffectItems[0];
                AppViewModel.CharFloatAmount = settings.CharFloatAmount;
                AppViewModel.CharScaleAmount = settings.CharScaleAmount;
                AppViewModel.GlowAmount = settings.GlowAmount;
                AppViewModel.LongSyllableThreshold = settings.LongSyllableThreshold;
                AppViewModel.PlayingLineTopOffsetPercent = settings.PlayingLineTopOffsetPercent;
                AppViewModel.TranslatedOpacityPercent = settings.TranslatedOpacityPercent;
                AppViewModel.UnplayedOpacityPercent = settings.UnplayedOpacityPercent;
                AppViewModel.TargetFrameRate = settings.TargetFrameRate;
                AppViewModel.EnableAdvancedLyricsEffect = settings.EnableAdvancedLyricsEffect;
                AppViewModel.ScrollEasingType = settings.ScrollEasingType;
                AppViewModel.ScrollEasingMode = settings.ScrollEasingMode;
                AppViewModel.PlayOrPauseShortcut = settings.PlayOrPauseShortcut;
                AppViewModel.NextSongShortcut = settings.NextSongShortcut;
                AppViewModel.PreviousSongShortcut = settings.PreviousSongShortcut;
                AppViewModel.VolumeUpShortcut = settings.VolumeUpShortcut;
                AppViewModel.VolumeDownShortcut = settings.VolumeDownShortcut;
                AppViewModel.TogglePlayingDetailShortcut = settings.TogglePlayingDetailShortcut;
                AppViewModel.BackShortcut = settings.BackShortcut;
                AppViewModel.ShowWindowShortcut = settings.ShowWindowShortcut;
                AppViewModel.ToggleFullScreenShortcut = settings.ToggleFullScreenShortcut;
                AppSettings.EnableGlobalHotKey = settings.EnableGlobalHotKey;
                AppViewModel.EnableGlobalHotKey = settings.EnableGlobalHotKey;
                AppSettings.IsTrimOnHideEnabled = settings.IsTrimOnHideEnabled;
                AppViewModel.IsTrimOnHideEnabled = settings.IsTrimOnHideEnabled;
                AppSettings.IsTrimAfterPlaybackEnabled = settings.IsTrimAfterPlaybackEnabled;
                AppViewModel.IsTrimAfterPlaybackEnabled = settings.IsTrimAfterPlaybackEnabled;
                AppViewModel.ArtistSplitSymbols = settings.ArtistSplitSymbols;
                AppViewModel.PlayingDetailAlignment = settings.PlayingDetailAlignment;
                AppViewModel.UsePlayingDetailAlignmentInPortrait = settings.UsePlayingDetailAlignmentInPortrait;
                AppViewModel.IsMusicInfoVisible = settings.IsMusicInfoVisible;
                LoadSettingsToAppViewModel();
            }
        }

        private void LoadSettingsToAppViewModel()
        {
            if (AppViewModel.BackdropType != "CustomAcrylicStyle")
            {
                AppViewModel.IsColorPickerVisible = false;
            }
            else
            {
                AppViewModel.IsColorPickerVisible = true;
            }
            AppViewModel.Version = $"{Windows.ApplicationModel.Package.Current.Id.Version.Major}.{Windows.ApplicationModel.Package.Current.Id.Version.Minor}.{Windows.ApplicationModel.Package.Current.Id.Version.Build}.{Windows.ApplicationModel.Package.Current.Id.Version.Revision}";
            _ = AppViewModel.GetWasapiDeviceAsync();
        }

        public async Task SaveSettingAsync()
        {
            SaveSettings newSettings = SaveCurrentSettings(new SaveSettings());
            await WriteSettingsToJson(newSettings);
        }

        public async Task SaveEqualizerSettingAsync()
        {
            SaveEqualizer equalizerSettings = await GetEqualizer();
            SaveEqualizer newEqualizer = SaveEqualizeSettings(new SaveEqualizer(), equalizerSettings.EqualizerStr);
            if (equalizerSettings is null)
            {
                await InsertEqualizer(newEqualizer);
            }
            else
            {
                newEqualizer.Id = equalizerSettings.Id;
                await UpdateEqualizer(newEqualizer);
            }
        }

        private SaveEqualizer SaveEqualizeSettings(SaveEqualizer newEqualizer, string equalizerStr = null)
        {
            newEqualizer.EqualizerStr = equalizerStr ?? AppSettings.EqualizerStr;
            newEqualizer.IsEqualizerEnabled = AppSettings.IsEqualizerEnabled;
            newEqualizer.EqualizerPreset = AppSettings.EqualizerPreset;
            return newEqualizer;
        }

        private SaveSettings SaveCurrentSettings(SaveSettings newSettings)
        {
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
            newSettings.BassOutputDeviceId = AppSettings.BassOutputDeviceId;
            newSettings.Latency = AppViewModel.Latency;
            newSettings.DefaultEntry = AppViewModel.DefaultEntryComboBoxTag;
            newSettings.DefaultPlayList = AppViewModel.DefaultPlayListComboBoxTag;
            newSettings.AppStyle = AppViewModel.BackdropType;
            newSettings.AppTheme = AppViewModel.ThemeType;
            newSettings.IsRunningBackend = AppViewModel.IsRunningBackend;
            newSettings.IsAutoLyricsEnabled = AppViewModel.IsAutoLyricsEnabled;
            newSettings.IsAutoCoverEnabled = AppViewModel.IsAutoCoverEnabled;
            newSettings.DsdGain = AppViewModel.DsdGain;
            newSettings.CoverSize = AppViewModel.CoverSize;
            newSettings.Win2dTextEffectType = AppViewModel.Win2dTextEffectType.Value;
            newSettings.IsFluidBackgroundEnabled = AppViewModel.IsFluidBackgroundEnabled;
            newSettings.BackgroundShader = (int)AppViewModel.BackgroundShader;
            newSettings.IsFogEffectEnabled = AppViewModel.IsFogEffectEnabled;
            newSettings.IsSnowEffectEnabled = AppViewModel.IsSnowEffectEnabled;
            newSettings.IsRaindropEffectEnabled = AppViewModel.IsRaindropEffectEnabled;
            newSettings.IsFolderWatchEnabled = AppViewModel.IsFolderWatchEnabled;
            newSettings.IsCustomAppSize = AppViewModel.IsCustomAppSize;
            newSettings.AppHeight = AppViewModel.AppHeight;
            newSettings.AppWidth = AppViewModel.AppWidth;
            newSettings.GlobalFont = AppViewModel.FontFamily.FontFamily.Source;
            newSettings.CustomAcrylicOpacity = AppViewModel.CustomOpacity;
            newSettings.CustomColorArgb = (uint)((AppViewModel.CustomColor.A << 24) | (AppViewModel.CustomColor.R << 16) | (AppViewModel.CustomColor.G << 8) | AppViewModel.CustomColor.B);
            newSettings.IsCustomLyricsColorEnabled = AppViewModel.IsCustomLyricsColorEnabled;
            newSettings.LyricsCustomColorRgb = (uint)((AppViewModel.LyricsCustomColor.R << 16) | (AppViewModel.LyricsCustomColor.G << 8) | AppViewModel.LyricsCustomColor.B);
            newSettings.IsUpdateBackDrop = AppViewModel.IsUpdateBackDrop;
            newSettings.LyricsAlignment = AppViewModel.LyricsAlignment;
            newSettings.LyricsMargin = (int)AppViewModel.LyricsMargin.Left;
            newSettings.GlobalFontSize = AppViewModel.GlobalFontSize;
            newSettings.IsGlobalFontSizeEnabled = AppViewModel.IsGlobalFontSizeEnabled;
            newSettings.MusicCoverCache = AppViewModel.MusicCoverCache;
            newSettings.IsDopEnabled = AppViewModel.IsDopEnabled;
            newSettings.DsdPcmFreq = AppViewModel.DsdPcmFreq;
            newSettings.IsFadeEnabled = AppViewModel.IsFadeEnabled;
            newSettings.LyricsBlurAmount = AppViewModel.LyricsBlurAmount;
            newSettings.UseImageDominantTheme = AppViewModel.UseImageDominantTheme;
            newSettings.EnableLightWave = AppViewModel.EnableLightWave;
            newSettings.PaletteAlgorithm = (int)AppViewModel.PaletteAlgorithm;
            newSettings.IsWin2dAnimatedText = AppViewModel.IsWin2dAnimatedText;
            newSettings.IsWin2dCoverImageControlEnable = AppViewModel.IsWin2dCoverImageControlEnable;
            newSettings.CharFloatAmount = AppViewModel.CharFloatAmount;
            newSettings.CharScaleAmount = AppViewModel.CharScaleAmount;
            newSettings.GlowAmount = AppViewModel.GlowAmount;
            newSettings.LongSyllableThreshold = AppViewModel.LongSyllableThreshold;
            newSettings.PlayingLineTopOffsetPercent = AppViewModel.PlayingLineTopOffsetPercent;
            newSettings.TranslatedOpacityPercent = AppViewModel.TranslatedOpacityPercent;
            newSettings.UnplayedOpacityPercent = AppViewModel.UnplayedOpacityPercent;
            newSettings.TargetFrameRate = AppViewModel.TargetFrameRate;
            newSettings.EnableAdvancedLyricsEffect = AppViewModel.EnableAdvancedLyricsEffect;
            newSettings.ScrollEasingType = AppViewModel.ScrollEasingType;
            newSettings.ScrollEasingMode = AppViewModel.ScrollEasingMode;
            newSettings.PlayOrPauseShortcut = AppViewModel.PlayOrPauseShortcut;
            newSettings.NextSongShortcut = AppViewModel.NextSongShortcut;
            newSettings.PreviousSongShortcut = AppViewModel.PreviousSongShortcut;
            newSettings.VolumeUpShortcut = AppViewModel.VolumeUpShortcut;
            newSettings.VolumeDownShortcut = AppViewModel.VolumeDownShortcut;
            newSettings.TogglePlayingDetailShortcut = AppViewModel.TogglePlayingDetailShortcut;
            newSettings.BackShortcut = AppViewModel.BackShortcut;
            newSettings.ShowWindowShortcut = AppViewModel.ShowWindowShortcut;
            newSettings.ToggleFullScreenShortcut = AppViewModel.ToggleFullScreenShortcut;
            newSettings.EnableGlobalHotKey = AppViewModel.EnableGlobalHotKey;
            newSettings.IsTrimOnHideEnabled = AppViewModel.IsTrimOnHideEnabled;
            newSettings.IsTrimAfterPlaybackEnabled = AppViewModel.IsTrimAfterPlaybackEnabled;
            newSettings.ArtistSplitSymbols = AppViewModel.ArtistSplitSymbols;
            newSettings.PlayingDetailAlignment = AppViewModel.PlayingDetailAlignment;
            newSettings.UsePlayingDetailAlignmentInPortrait = AppViewModel.UsePlayingDetailAlignmentInPortrait;
            newSettings.IsDesktopLyricsEnabled = AppSettings.IsDesktopLyricsEnabled;
            newSettings.IsDesktopLyricsLocked = AppSettings.IsDesktopLyricsLocked;
            newSettings.DesktopLyricsFontSize = AppSettings.DesktopLyricsFontSize;
            newSettings.DesktopLyricsFontFamily = AppSettings.DesktopLyricsFontFamily;
            newSettings.DesktopLyricsColorRgb = AppSettings.DesktopLyricsColorRgb;
            newSettings.IsDesktopLyricsOutlineEnabled = AppSettings.IsDesktopLyricsOutlineEnabled;
            newSettings.DesktopLyricsFontWeight = AppSettings.DesktopLyricsFontWeight;
            newSettings.DesktopLyricsOutlineWidth = AppSettings.DesktopLyricsOutlineWidth;
            newSettings.IsMusicInfoVisible = AppViewModel.IsMusicInfoVisible;
            return newSettings;
        }

        public async Task RemoveMusic(int musicId)
        {
            try
            {
                await _dbConnection.DeleteAsync<Music>(musicId);
                AppViewModel.SongsSource.Clear();
                AppViewModel.SongsSource.AddRange(await _dbConnection.Table<Music>().ToListAsync());
                AppViewModel.NotifySongsSourceChanged();
                var usbMusicGroups = AppData.MusicOnUsbDevice.AsValueEnumerable()
                    .GroupBy(u => u.Title)
                    .ToDictionary(g => g.Key, g => g.AsValueEnumerable().ToList());
                foreach (var music in AppViewModel.SongsSource)
                {
                    music.IsExistOnDevice = 0;
                    if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
                    {
                        music.IsExistOnDevice = 1;
                        foreach (var usbMusic in matchingItems)
                        {
                            if (music.Author == usbMusic.Author &&
                                music.Album == usbMusic.Album &&
                                music.Extension == usbMusic.Extension)
                            {
                                music.IsExistOnDevice = 2;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"RemoveMusic 删除音乐时出错: {e.Message}");
            }
        }

        public async Task AddToFavourite(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }

        public Music? LoadCurrentPlayingMusic(int? lastPlayedMusicId)
        {
            return AppViewModel.SongsSource.FirstOrDefault(m => m.Id == lastPlayedMusicId);
        }

        public async Task SavePlayStateAsync(SavePlayState playState, IEnumerable<Music> currentPlayingList)
        {
            try
            {
                await _dbConnection.DeleteAllAsync<LastPlayListState>().ConfigureAwait(false);
                var musicIds = string.Join(',', currentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                await _dbConnection.InsertAsync(new LastPlayListState { PlayListMusicIds = musicIds }).ConfigureAwait(false);

                string path = PlayStatePath;
                string json = JsonSerializer.Serialize(playState, PlayStateJsonContext.Default.SavePlayState);
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
                _currentPlayState = playState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SavePlayStateAsync 异步保存播放状态时出错: {ex.Message}");
            }
        }

        public void WritePlayStateJsonSync()
        {
            if (_currentPlayState == null) return;
            try
            {
                string path = PlayStatePath;
                string json = JsonSerializer.Serialize(_currentPlayState, PlayStateJsonContext.Default.SavePlayState);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WritePlayStateJsonSync 写入播放状态 JSON 时出错: {ex.Message}");
            }
        }

        public async Task<List<Music>> GetMusicListByFolder(StorageFolder folder)
        {
            var musicFiles = new List<(Music Music, string Lyrics)>();
            await addFolderService.GetMusicFilesRecursive(folder, musicFiles);
            return musicFiles.AsValueEnumerable().Select(r => r.Music).ToList();
        }

        public async Task ScanFolderAsync(StorageFolder folder, int folderId)
        {
            var musicFiles = new List<(Music Music, string Lyrics)>();
            List<SubFolder> subFolders = AutoRescanService.RecordInitialFolderTimes(folder.Path, folderId);
            await InsertSubFolders(subFolders);
            await addFolderService.GetMusicFilesRecursive(folder, musicFiles);
            var existingMusicPaths = await _dbConnection.Table<Music>()
                .ToListAsync()
                .ContinueWith(t => t.Result.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));

            // 优化9: 用 HashSet 做路径查重，O(1) 代替 O(n)
            var newMusicFiles = musicFiles.AsValueEnumerable()
                .Where(r => !existingMusicPaths.Contains(r.Music.Path))
                .ToList();

            if (newMusicFiles.Count != 0)
            {
                await _dbConnection.InsertAllAsync(newMusicFiles.AsValueEnumerable().Select(r => r.Music).ToList());
                await SaveEmbeddedLyricsAsync(newMusicFiles);
            }
        }

        public async Task RemoveFolder(int folderId)
        {
            var folderToRemove = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRemove is not null)
            {
                var musicFilesToRemove = await _dbConnection.Table<Music>()
                    .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                    .ToListAsync();

                foreach (var musicFile in musicFilesToRemove)
                {
                    await _dbConnection.DeleteAsync(musicFile);
                    await _dbConnection.DeleteAsync<MusicLyrics>(musicFile.Id);
                }

                var subfoldersToRemove = await _dbConnection.Table<SubFolder>()
                    .Where(sf => sf.Path.StartsWith(folderToRemove.Path))
                    .ToListAsync();
                foreach (var subfolder in subfoldersToRemove)
                {
                    await _dbConnection.DeleteAsync(subfolder);
                }

                await _dbConnection.DeleteAsync(folderToRemove);
            }
        }

        public async Task<Folder> GetFolder(int folderId)
        {
            return await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
        }

        public async Task CheckFolderBeforeAdd(StorageFolder folder)
        {
            var existingFolders = await _dbConnection.Table<Folder>().ToListAsync();

            bool folderAlreadyExists = existingFolders.AsValueEnumerable().Any(f =>
                folder.Path.StartsWith(f.Path) || f.Path.StartsWith(folder.Path));

            if (!folderAlreadyExists)
            {
                var foldersToRemove = existingFolders.AsValueEnumerable()
                    .Where(f => folder.Path.StartsWith(f.Path))
                    .ToList();

                foreach (var folderToRemove in foldersToRemove)
                {
                    var musicFilesToRemove = await _dbConnection.Table<Music>()
                        .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                        .ToListAsync();

                    foreach (var musicFile in musicFilesToRemove)
                    {
                        await _dbConnection.DeleteAsync(musicFile);
                        await _dbConnection.DeleteAsync<MusicLyrics>(musicFile.Id);
                    }

                    await _dbConnection.DeleteAsync(folderToRemove);
                }

                var newFolder = new Folder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Type = "本地"
                };
                await _dbConnection.InsertAsync(newFolder);
                await ScanFolderAsync(folder, newFolder.Id);
            }
        }

        public async Task<List<StorageFile>> GetAllFilesInFolderAndSubfolders(StorageFolder folder)
        {
            var allFiles = new List<StorageFile>();

            try
            {
                var currentFiles = await folder.GetFilesAsync();
                allFiles.AddRange(currentFiles);
                var subFolders = await folder.GetFoldersAsync();
                foreach (var subFolder in subFolders)
                {
                    var subFolderFiles = await GetAllFilesInFolderAndSubfolders(subFolder);
                    allFiles.AddRange(subFolderFiles);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetAllFilesInFolderAndSubfolders 获取文件时出错: {ex.Message}");
            }

            return allFiles;
        }

        private async Task<(Music Music, string Lyrics)> UpdateMusic(Music music)
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(music.Path);
            var (newMusic, lyrics) = await ToolUtils.GetMusicInfo(storageFile);
            if (newMusic is null) return (music, "");
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                music.Title = newMusic.Title;
                music.Author = newMusic.Author;
                music.Duration = newMusic.Duration;
                music.Album = newMusic.Album;
                music.FolderPath = newMusic.FolderPath;
                music.LastLevelFolderPath = newMusic.LastLevelFolderPath;
                music.BitDepth = newMusic.BitDepth;
                music.BitRate = newMusic.BitRate;
                music.SampleRate = newMusic.SampleRate;
                music.Channel = newMusic.Channel;
                music.TrackNumber = newMusic.TrackNumber;
                music.DiskNumber = newMusic.DiskNumber;
                music.Year = newMusic.Year;
                music.UpdateTime = newMusic.UpdateTime;
                music.CreateTime = newMusic.CreateTime;
            });
            return (music, lyrics ?? "");
        }

        public async Task RescanFolder(int folderId)
        {
            var folderToRescan = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRescan is not null)
            {
                try
                {
                    await RescanFolderByPath(folderToRescan.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"RescanFolder 重新扫描文件夹时出错: {ex.Message}");
                }
            }
        }

        public async Task RescanFolderByPath(string folderPath, bool isUpdate = true, bool isSingleFolder = false)
        {
            var musicPaths = await Task.Run(() =>
                isSingleFolder ? EnumerateMusicFilesInDirectory(folderPath) : EnumerateAllMusicFiles(folderPath));

            var filePaths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in musicPaths)
                filePaths.TryAdd(path, true);

            List<Music> musicFilesInFolder;
            if (isSingleFolder)
            {
                musicFilesInFolder = await _dbConnection.Table<Music>()
                    .Where(m => m.FolderPath == folderPath)
                    .ToListAsync();
            }
            else
            {
                musicFilesInFolder = await _dbConnection.Table<Music>()
                   .Where(m => m.FolderPath.Contains(folderPath))
                   .ToListAsync();
            }

            // 优化13: 并行检查改预分配数组
            var checkTasks = new Task[musicFilesInFolder.Count];
            // 优化14: toDelete/toUpdate 在并行中使用 ConcurrentBag（局部），安全且无共享状态
            var toDeleteBag = new ConcurrentBag<Music>();
            var toUpdateBag = new ConcurrentBag<Music>();
            for (int i = 0; i < musicFilesInFolder.Count; i++)
            {
                var music = musicFilesInFolder[i];
                checkTasks[i] = CheckMusicExistsAsync(music, filePaths, toDeleteBag, toUpdateBag);
            }
            await Task.WhenAll(checkTasks);

            // 优化15: 批量删除，预分配数组
            var deleteList = toDeleteBag.ToList();
            var deleteTasks = new Task[deleteList.Count];
            for (int i = 0; i < deleteList.Count; i++)
            {
                var music = deleteList[i];
                deleteTasks[i] = DeleteMusicAsync(music);
            }
            await Task.WhenAll(deleteTasks);

            // 优化16: 批量更新，预分配数组
            var updateList = toUpdateBag.ToList();
            var updateTasks = new Task<(Music Music, string Lyrics)>[updateList.Count];
            for (int i = 0; i < updateList.Count; i++)
            {
                var music = updateList[i];
                updateTasks[i] = UpdateMusicWithSemaphoreAsync(music);
            }
            var results = await Task.WhenAll(updateTasks);
            var validResults = results.AsValueEnumerable().Where(r => r.Music is not null).ToList();
            if (validResults.Count != 0)
            {
                await _dbConnection.UpdateAllAsync(validResults.AsValueEnumerable().Select(r => r.Music).ToList());
                await SaveEmbeddedLyricsAsync(validResults);
            }

            // 优化17: 新增文件批量处理，预分配数组
            var filePathKeys = filePaths.Keys.ToList();
            var addTasks = new Task<(Music? Music, string Lyrics)>[filePathKeys.Count];
            for (int i = 0; i < filePathKeys.Count; i++)
            {
                var path = filePathKeys[i];
                addTasks[i] = AddNewMusicAsync(path);
            }
            var addResults = await Task.WhenAll(addTasks);
            var validMusic = addResults.AsValueEnumerable().Where(r => r.Music is not null).ToList();
            if (validMusic.Count != 0)
            {
                await _dbConnection.InsertAllAsync(validMusic.AsValueEnumerable().Select(r => r.Music!).ToList());
                await SaveEmbeddedLyricsAsync(validMusic);
            }

            if (isUpdate)
            {
                await App.Services.GetRequiredService<AppViewModel>().RefreshSongsSourceAsync();
            }
        }

        // 优化18: 提取具名私有方法，编译器可生成 struct 状态机（相比 async lambda 减少堆分配）
        private async Task AddFilePathAsync(StorageFile file, ConcurrentDictionary<string, bool> filePaths)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                if (ToolUtils.IsMusicFile(file.FileType))
                {
                    filePaths.TryAdd(file.Path, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AddFilePathAsync 添加文件路径时出错: {ex.Message}");
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        private async Task CheckMusicExistsAsync(Music music, ConcurrentDictionary<string, bool> filePaths,
            ConcurrentBag<Music> toDelete, ConcurrentBag<Music> toUpdate)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                if (!filePaths.ContainsKey(music.Path))
                {
                    toDelete.Add(music);
                }
                else
                {
                    toUpdate.Add(music);
                    filePaths.TryRemove(music.Path, out _);
                }
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        private async Task DeleteMusicAsync(Music music)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                await _dbConnection.DeleteAsync(music);
                await _dbConnection.DeleteAsync<MusicLyrics>(music.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"DeleteMusicAsync 删除音乐文件时出错: {ex.Message}");
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        private async Task<(Music Music, string Lyrics)> UpdateMusicWithSemaphoreAsync(Music music)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                return await UpdateMusic(music);
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        private async Task<(Music? Music, string Lyrics)> AddNewMusicAsync(string path)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == path).FirstOrDefaultAsync();
                if (existingMusic is not null)
                {
                    return (null, "");
                }
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                var (music, lyrics) = await ToolUtils.GetMusicInfo(storageFile);
                return (music, lyrics ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AddNewMusicAsync 添加新音乐文件时出错: {ex.Message}");
                return (null, "");
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        public async Task<int> RescanFolderWithOutUpdateAll(string folderPath, bool isSingleFolder = false)
        {
            var toDelete = new ConcurrentBag<Music>();

            var musicPaths = await Task.Run(() =>
                isSingleFolder ? EnumerateMusicFilesInDirectory(folderPath) : EnumerateAllMusicFiles(folderPath));

            var filePaths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in musicPaths)
                filePaths.TryAdd(path, true);

            List<Music> musicFilesInFolder;
            if (isSingleFolder)
            {
                musicFilesInFolder = await _dbConnection.Table<Music>()
                    .Where(m => m.FolderPath == folderPath)
                    .ToListAsync();
            }
            else
            {
                musicFilesInFolder = await _dbConnection.Table<Music>()
                   .Where(m => m.FolderPath.Contains(folderPath))
                   .ToListAsync();
            }

            var checkTasks = new Task[musicFilesInFolder.Count];
            for (int i = 0; i < musicFilesInFolder.Count; i++)
            {
                var music = musicFilesInFolder[i];
                checkTasks[i] = CheckMusicExistsForRescanAsync(music, filePaths, toDelete);
            }
            await Task.WhenAll(checkTasks);

            var deleteList = toDelete.ToList();
            var deleteTasks = new Task[deleteList.Count];
            for (int i = 0; i < deleteList.Count; i++)
            {
                var music = deleteList[i];
                deleteTasks[i] = DeleteMusicAsync(music);
            }
            await Task.WhenAll(deleteTasks);

            var filePathKeys = filePaths.Keys.ToList();
            var addTasks = new Task<(Music? Music, string Lyrics)>[filePathKeys.Count];
            for (int i = 0; i < filePathKeys.Count; i++)
            {
                var path = filePathKeys[i];
                addTasks[i] = AddNewMusicAsync(path);
            }
            var addResults = await Task.WhenAll(addTasks);
            var validMusic = addResults.AsValueEnumerable().Where(r => r.Music is not null).ToList();
            if (validMusic.Count != 0)
            {
                await _dbConnection.InsertAllAsync(validMusic.AsValueEnumerable().Select(r => r.Music!).ToList());
                await SaveEmbeddedLyricsAsync(validMusic);
            }

            return validMusic.Count;
        }

        private async Task CheckMusicExistsForRescanAsync(Music music, ConcurrentDictionary<string, bool> filePaths, ConcurrentBag<Music> toDelete)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                if (!filePaths.ContainsKey(music.Path))
                {
                    toDelete.Add(music);
                }
                else
                {
                    filePaths.TryRemove(music.Path, out _);
                }
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        public async Task AddMusicList(IEnumerable<Music> _toAdd)
        {
            var toAddList = _toAdd is ICollection<Music> c ? new List<Music>(c) : _toAdd.ToList();
            if (toAddList.Count == 0) return;

            var validMusic = new List<(Music Music, string Lyrics)>();
            var channel = Channel.CreateUnbounded<Music>();
            int workerCount = Math.Min(4, toAddList.Count);
            var workers = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
                workers[i] = WorkerLoop(channel.Reader);

            foreach (var m in toAddList)
                channel.Writer.TryWrite(m);
            channel.Writer.Complete();

            await Task.WhenAll(workers);

            if (validMusic.Count != 0)
                await _dbConnection.InsertAllAsync(validMusic.AsValueEnumerable().Select(r => r.Music).ToList());
            await SaveEmbeddedLyricsAsync(validMusic);

            async Task WorkerLoop(ChannelReader<Music> reader)
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var m))
                    {
                        var (music, lyrics) = await AddMusicFromPathAsync(m.Path);
                        if (music is not null)
                            lock (validMusic) validMusic.Add((music, lyrics));
                    }
                }
            }
        }

        private async Task<(Music? Music, string Lyrics)> AddMusicFromPathAsync(string path)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                var (music, lyrics) = await ToolUtils.GetMusicInfo(storageFile);
                return (music, lyrics ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AddMusicFromPathAsync 添加新音乐文件时出错: {ex.Message}");
                return (null, "");
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        public async Task UpdateMusicList(IEnumerable<Music> _toUpdate)
        {
            var toUpdateList = _toUpdate is ICollection<Music> c ? new List<Music>(c) : _toUpdate.ToList();
            if (toUpdateList.Count == 0) return;

            var validResults = new List<(Music Music, string Lyrics)>();
            var channel = Channel.CreateUnbounded<Music>();
            int workerCount = Math.Min(4, toUpdateList.Count);
            var workers = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
                workers[i] = WorkerLoop(channel.Reader);

            foreach (var music in toUpdateList)
                channel.Writer.TryWrite(music);
            channel.Writer.Complete();

            await Task.WhenAll(workers);

            if (validResults.Count != 0)
                await _dbConnection.UpdateAllAsync(validResults.AsValueEnumerable().Select(r => r.Music).ToList());
            await SaveEmbeddedLyricsAsync(validResults);

            async Task WorkerLoop(ChannelReader<Music> reader)
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var music))
                    {
                        var result = await UpdateMusicWithSemaphoreAsync(music);
                        lock (validResults) validResults.Add(result);
                    }
                }
            }
        }

        public async Task DeletedMusicList(IEnumerable<Music> toDelete)
        {
            var toDeleteList = toDelete is ICollection<Music> c ? new List<Music>(c) : toDelete.ToList();
            if (toDeleteList.Count == 0) return;

            var channel = Channel.CreateUnbounded<Music>();
            int workerCount = Math.Min(4, toDeleteList.Count);
            var workers = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
                workers[i] = WorkerLoop(channel.Reader);

            foreach (var music in toDeleteList)
                channel.Writer.TryWrite(music);
            channel.Writer.Complete();

            await Task.WhenAll(workers);

            async Task WorkerLoop(ChannelReader<Music> reader)
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var music))
                    {
                        await DeleteMusicAsync(music);
                    }
                }
            }
        }

        public async Task<List<UsbDeviceMusic>> GetUsbDeviceMusics(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public async Task<List<UsbDeviceMusic>> RescanUsbDeviceFolderByPath(List<UsbDeviceMusic> usbDeviceMusics, string uniqueDeviceId, string folderPath, bool isSingleFolder = false)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<StorageFile> files;
            List<UsbDeviceMusic> musicFilesInFolder;

            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files = [.. currentFiles];
                musicFilesInFolder = usbDeviceMusics.AsValueEnumerable().Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            }
            else
            {
                files = await GetAllFilesInFolderAndSubfolders(folder);
                musicFilesInFolder = usbDeviceMusics.AsValueEnumerable()
                   .Where(m => m.Path.Contains(folderPath)).ToList();
            }

            // 优化24: 用 HashSet 而非逐步 Add，构造时一次性去重
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    if (ToolUtils.IsMusicFile(file.FileType))
                    {
                        filePaths.Add(file.Path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"RescanUsbDeviceFolderByPath 添加文件路径时出错: {ex.Message}");
                }
            }

            var toDelete = new List<UsbDeviceMusic>();
            foreach (var newMusic in musicFilesInFolder)
            {
                if (!filePaths.Contains(newMusic.Path))
                {
                    toDelete.Add(newMusic);
                }
                else
                {
                    filePaths.Remove(newMusic.Path);
                }
            }

            foreach (var music in toDelete)
            {
                await _dbConnection.DeleteAsync(music);
                musicFilesInFolder.Remove(music);
            }

            // 优化25: 并行获取文件信息后批量 InsertAllAsync，减少 N 次数据库往返为 1 次
            var usbDeviceMusicIndexByPath = new HashSet<string>(
                usbDeviceMusics.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);

            var newPathList = filePaths
                .Where(p => !usbDeviceMusicIndexByPath.Contains(p))
                .ToList();

            var fetchTasks = newPathList.Select(async path =>
            {
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                return addFolderService.GetUsbDeviceMusicInfo(storageFile, folder.Path, uniqueDeviceId);
            });
            var fetchResults = await Task.WhenAll(fetchTasks);
            var usbDeviceMusicsInsertList = fetchResults.Where(r => r is not null).ToList();

            if (usbDeviceMusicsInsertList.Count > 0)
            {
                await _dbConnection.InsertAllAsync(usbDeviceMusicsInsertList);
            }

            return usbDeviceMusicsInsertList;
        }

        public async Task ScanUsbDeviceAsync(string drivePath, string uniqueDeviceId)
        {
            try
            {
                var usbDeviceMusics = await GetUsbDeviceMusics(uniqueDeviceId) ?? [];
                var diskPaths = await Task.Run(() =>
                {
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in EnumerateAllMusicFiles(drivePath))
                        paths.Add(path);
                    return paths;
                });

                var toRemove = usbDeviceMusics.Where(m => !diskPaths.Contains(m.Path)).ToList();
                foreach (var music in toRemove)
                    await _dbConnection.DeleteAsync(music);

                var existingPaths = new HashSet<string>(
                    usbDeviceMusics.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);
                var newPaths = diskPaths.Where(p => !existingPaths.Contains(p)).ToList();

                if (newPaths.Count > 0)
                {
                    var fetchTasks = newPaths.Select(path =>
                        Task.Run(() => addFolderService.GetUsbDeviceMusicInfoByPath(path, drivePath, uniqueDeviceId)));
                    var fetchResults = await Task.WhenAll(fetchTasks);
                    var newMusic = fetchResults.Where(r => r is not null).ToList();
                    if (newMusic.Count > 0)
                        await _dbConnection.InsertAllAsync(newMusic);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ScanUsbDeviceAsync 扫描USB设备失败: {ex.Message}");
            }
        }

        private static readonly string[] _musicPatterns =
            [".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".oga", ".aiff", ".aif", ".m4a", ".dsf", ".dff", ".ape", ".opus", ".wv"];

        private static List<string> EnumerateAllMusicFiles(string rootPath)
        {
            var paths = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    if (HasMusicExtension(file))
                        paths.Add(file);
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
            {
            }
            return paths;
        }

        private static List<string> EnumerateMusicFilesInDirectory(string folderPath)
        {
            var paths = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (HasMusicExtension(file))
                        paths.Add(file);
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
            {
            }
            return paths;
        }

        public async Task<string?> GetRecordedVersionAsync()
        {
            try
            {
                string path = VersionRecordPath;
                if (!File.Exists(path))
                    return null;
                string json = await File.ReadAllTextAsync(path);
                var record = JsonSerializer.Deserialize(json, VersionJsonContext.Default.VersionRecord);
                return record?.Version;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetRecordedVersionAsync 读取版本记录时出错: {ex.Message}");
                return null;
            }
        }

        public async Task SaveCurrentVersionAsync(string version)
        {
            try
            {
                string path = VersionRecordPath;
                var record = new VersionRecord { Version = version };
                string json = JsonSerializer.Serialize(record, VersionJsonContext.Default.VersionRecord);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveCurrentVersionAsync 保存版本记录时出错: {ex.Message}");
            }
        }

        private static bool HasMusicExtension(string filePath)
        {
            ReadOnlySpan<char> span = filePath;
            int dot = span.LastIndexOf('.');
            if (dot < 0) return false;
            ReadOnlySpan<char> ext = span[dot..];
            foreach (var pattern in _musicPatterns)
            {
                if (MemoryExtensions.Equals(ext, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}