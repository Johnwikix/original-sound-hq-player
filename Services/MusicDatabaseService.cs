using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SQLite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    public partial class MusicDatabaseService
    {
        private SQLiteAsyncConnection _dbConnection;
        private string DbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        private string? _settingsPath;
        private string SettingsPath => _settingsPath ??= GetSettingsFilePath();
        private string PlayStatePath => GetPlayStateFilePath();
        private string VersionRecordPath => GetVersionRecordFilePath();
        private readonly AddFolderService addFolderService = new();
        private SaveSettings _currentSettings;
        public SaveSettings CurrentSettings => _currentSettings;
        // 设置文件读写互斥：写不并发（避免 IOException 丢更新），读不撞写（避免读到半截 JSON）
        private readonly SemaphoreSlim _settingsIoGate = new(1, 1);
        private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
        private SettingsSaveQueue? _settingsSaveQueue;
        private SettingsSnapshotFactory? _settingsCapture;
        public void AttachSettingsCapture(SettingsSnapshotFactory capture) => _settingsCapture = capture;
        // 播放状态文件同一套互斥（同步读写路径用 Wait() 阻塞进入，临界区仅一次小文件 IO）
        private readonly SemaphoreSlim _playStateIoGate = new(1, 1);
        // 桌面歌词窗口状态文件仅同步读写，用 lock 即可
        private readonly object _desktopLyricsStateFileLock = new();
        // 版本记录文件同一套互斥
        private readonly SemaphoreSlim _versionRecordIoGate = new(1, 1);
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
                await _dbConnection.CreateTableAsync<PendingMetadataWrite>();
                await _dbConnection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Music_Path_NoCase ON Music(Path COLLATE NOCASE)");
                await _dbConnection.CreateTableAsync<MusicLyrics>();
                await _dbConnection.CreateTableAsync<Folder>();
                await _dbConnection.CreateTableAsync<SaveEqualizer>();
                await _dbConnection.CreateTableAsync<SaveEqualizerPreset>();
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
            lock (_desktopLyricsStateFileLock)
            {
                string path = GetDesktopLyricsStateFilePath();
                if (!File.Exists(path))
                {
                    return new SaveDesktopLyricsState();
                }
                try
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(path), DesktopLyricsStateJsonContext.Default.SaveDesktopLyricsState) ?? new SaveDesktopLyricsState();
                }
                catch (JsonException ex)
                {
                    // 损坏文件留底后按默认值继续，避免之后一次写入把事故固化成永久丢失
                    _logger.LogError(ex, $"DesktopLyricsState.json 解析失败，隔离损坏文件后按默认值继续: {ex.Message}");
                    TryPreserveCorruptFile(path);
                    return new SaveDesktopLyricsState();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"LoadDesktopLyricsState 读取桌面歌词窗口状态失败: {ex.Message}");
                    return new SaveDesktopLyricsState();
                }
            }
        }

        public void SaveDesktopLyricsState(SaveDesktopLyricsState state)
        {
            lock (_desktopLyricsStateFileLock)
            {
                try
                {
                    File.WriteAllText(GetDesktopLyricsStateFilePath(), JsonSerializer.Serialize(state, DesktopLyricsStateJsonContext.Default.SaveDesktopLyricsState));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"SaveDesktopLyricsState 写入桌面歌词窗口状态失败: {ex.Message}");
                }
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
            // The caller already enumerated the complete directory tree; remove this exact directory only.
            await DeletedMusicList(await GetSongsInFolderAsync(subFolderPath, false));
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

        public Task UpdateLyricsOffsetAsync(int musicId, int offset)
            => _dbConnection.ExecuteAsync("UPDATE Music SET LyricsOffsetMs = ? WHERE Id = ?", offset, musicId);

        // 定义接收 pragma_table_info 结果的类
        private class TableColumnInfo
        {
            public string Name { get; set; }
        }

        public async Task<(string? lyrics, string? transLrc, string? krc, string? tKrc)> GetLyricsAsync(int musicId)
        {
            var lyrics = await _dbConnection.FindAsync<MusicLyrics>(musicId);
            return (lyrics?.Lyrics, lyrics?.TranslatedLyrics, lyrics?.Krc, lyrics?.TKrc);
        }

        /// <summary>
        /// 按路径大小写不敏感匹配库内曲目并返回完整条目（走 IX_Music_Path_NoCase）；
        /// 未匹配或查询失败返回 null。供外部文件打开入口复用库内条目播放。
        /// </summary>
        public async Task<Music?> FindMusicByPathAsync(string path)
        {
            try
            {
                int musicId = await _dbConnection.ExecuteScalarAsync<int>(
                    "SELECT Id FROM Music WHERE Path = ? COLLATE NOCASE LIMIT 1", path);
                if (musicId <= 0) return null;
                return await _dbConnection.FindAsync<Music>(musicId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"FindMusicByPathAsync 按路径查找曲目失败: {path}: {ex.Message}");
                return null;
            }
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

        private BassPlayerIpc.Shared.AudioCorrectionStore? _audioCorrectionStore;
        private bool _settingsReadFailed;
        private byte[]? _lastSettingsBytes;
        private bool _correctionsMigrated;

        private readonly System.Threading.SemaphoreSlim _correctionSaveGate = new(1, 1);

        public Task SaveDeviceCorrectionsAsync(BassPlayerIpc.Shared.DeviceCorrections candidate)
        {
            AppSettings.DeviceCorrections = candidate.Validate();
            return SaveCurrentDeviceCorrectionsAsync();
        }

        public async Task SaveCurrentDeviceCorrectionsAsync()
        {
            await _correctionSaveGate.WaitAsync();
            try
            {
                var store = _audioCorrectionStore ?? throw new InvalidOperationException("Correction store unavailable.");
                var snapshot = AppSettings.DeviceCorrections;
                await Task.Run(() => store.SaveAsync(snapshot));
                // Saving an older snapshot must never roll back edits made while IO was in flight.
            }
            finally { _correctionSaveGate.Release(); }
        }

        public async Task<SaveSettings> GetSettings()
        {
            string path = SettingsPath;
            await _settingsIoGate.WaitAsync();
            try
            {
                var result = await BassPlayerIpc.Shared.AtomicSettingsFile.LoadAsync(path,
                    SettingsJsonContext.Default.SaveSettings, static () => new(), static () => new(),
                    onReset: preserved => _logger.LogWarning("Settings.json 已恢复默认值，损坏文件保留在 {Path}", preserved));
                _settingsReadFailed = false;
                _lastSettingsBytes = JsonSerializer.SerializeToUtf8Bytes(result, SettingsJsonContext.Default.SaveSettings);
                return result;
            }
            catch (Exception ex)
            {
                // 读取失败期间禁止覆盖已有配置。
                _logger.LogError(ex, "读取配置失败：{Path}", path);
                _settingsReadFailed = true;
                return _currentSettings ?? new SaveSettings();
            }
            finally
            {
                _settingsIoGate.Release();
            }
        }

        private bool TryPreserveCorruptFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                string backupPath = $"{path}.corrupt-{Guid.NewGuid():N}";
                File.Move(path, backupPath);
                _logger.LogInformation($"损坏的 JSON 文件已隔离为: {backupPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"备份损坏的 JSON 文件失败: {ex.Message}");
                return false;
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
            await _settingsIoGate.WaitAsync();
            try
            {
                if (_settingsReadFailed) throw new IOException("Settings read failed; refusing to overwrite the file.");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.SaveSettings);
                if (_lastSettingsBytes != null && bytes.AsSpan().SequenceEqual(_lastSettingsBytes) && File.Exists(SettingsPath)) return;
                var snapshot = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.SaveSettings)!;
                await BassPlayerIpc.Shared.AtomicSettingsFile.WriteAsync(SettingsPath, bytes);
                _currentSettings = snapshot;
                _lastSettingsBytes = bytes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WriteSettingsToJson 写入设置文件时出错: {ex.Message}");
            }
            finally
            {
                _settingsIoGate.Release();
            }
        }

        public async Task<SavePlayState> GetPlayState()
        {
            string path = PlayStatePath;
            if (!File.Exists(path))
            {
                return null;
            }
            await _playStateIoGate.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize(json, PlayStateJsonContext.Default.SavePlayState);
            }
            catch (JsonException ex)
            {
                // 损坏文件留底后按无状态继续，避免之后一次写入把事故固化成永久丢失
                _logger.LogError(ex, $"PlayState.json 解析失败，隔离损坏文件后按空状态继续: {ex.Message}");
                TryPreserveCorruptFile(path);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取配置失败：{Path}", path);
                return null;
            }
            finally
            {
                _playStateIoGate.Release();
            }
        }

        private async Task WritePlayStateToJson(SavePlayState state)
        {
            await _playStateIoGate.WaitAsync();
            try
            {
                string json = JsonSerializer.Serialize(state, PlayStateJsonContext.Default.SavePlayState);
                await File.WriteAllTextAsync(PlayStatePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WritePlayStateToJson 写入播放状态文件时出错: {ex.Message}");
            }
            finally
            {
                _playStateIoGate.Release();
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

        public async Task<List<SaveEqualizerPreset>> GetEqualizerPresets()
        {
            return await _dbConnection.Table<SaveEqualizerPreset>().OrderBy(p => p.Id).ToListAsync();
        }

        /// <summary>插入自定义预设。注意：InsertAsync 返回的是受影响行数而非主键，
        /// sqlite-net 会自动把自增 Id 回填到实体对象上，调用方不要用返回值覆盖 Id。</summary>
        public async Task InsertEqualizerPreset(SaveEqualizerPreset preset)
        {
            await _dbConnection.InsertAsync(preset);
        }

        public async Task UpdateEqualizerPreset(SaveEqualizerPreset preset)
        {
            await _dbConnection.UpdateAsync(preset);
        }

        public async Task DeleteEqualizerPreset(int presetId)
        {
            await _dbConnection.DeleteAsync<SaveEqualizerPreset>(presetId);
        }

        public async Task GetPlayListMusic()
        {
            var mappings = await _dbConnection.Table<PlayListMusic>().ToListAsync();
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                AppData.AllPlayListMusics = mappings;
                RefreshAllPlayListSongCounts();
            });
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
            var songs = await GetMusicListAsync();
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                AppViewModel.SongsSource.Clear();
                AppViewModel.SongsSource.AddRange(songs);
            });
            await InitalPlayListAsync();
            await GetPlayListMusic();
            var playlist = await LoadPlayList(songs);
            // 集合构造会捕获 DispatcherQueue，创建与绑定通知都必须在 UI 线程完成。
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                AppViewModel.SequentialPlayingList = new(playlist);
                AppViewModel.NotifySongsSourceChanged();
            });
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
            string path = PlayStatePath;
            if (!File.Exists(path))
            {
                _currentPlayState = new SavePlayState();
                return;
            }
            _playStateIoGate.Wait();
            try
            {
                string json = File.ReadAllText(path);
                _currentPlayState = JsonSerializer.Deserialize(json, PlayStateJsonContext.Default.SavePlayState)
                    ?? new SavePlayState();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, $"PlayState.json 解析失败，隔离损坏文件后按空状态继续: {ex.Message}");
                TryPreserveCorruptFile(path);
                _currentPlayState = new SavePlayState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LoadWindowState 读取播放状态文件时出错: {ex.Message}");
                _currentPlayState = new SavePlayState();
            }
            finally
            {
                _playStateIoGate.Release();
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
                AppSettings.EqualizerPreset = equalizerSettings.EqualizerPreset;
                // 只认 EqPreset v1 结构；旧版频段字典数据不迁移，直接重置为默认增益
                var preset = EqualizerHelper.Parse(equalizerSettings.EqualizerStr, equalizerSettings.EqualizerPreset)
                             ?? EqualizerHelper.Normalize(null);
                AppSettings.EqualizerBands = preset.Bands.ToArray();
                AppSettings.EqualizerStr = EqualizerHelper.Serialize(preset);
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
                var audio = await LoadAudioPreferencesAsync(settings);
                AppSettings.Dsp = audio.Dsp;
                _audioCorrectionStore = new BassPlayerIpc.Shared.AudioCorrectionStore(Path.Combine(Path.GetDirectoryName(SettingsPath)!, "AudioCorrections.json"));
                try
                {
                    if (_settingsReadFailed && !File.Exists(Path.Combine(Path.GetDirectoryName(SettingsPath)!, "AudioCorrections.json")))
                        throw new IOException("Cannot migrate corrections from unreadable settings.");
                    AppSettings.DeviceCorrections = await Task.Run(() => _audioCorrectionStore.LoadAsync(settings.DeviceCorrections));
                    if (_audioCorrectionStore.ResetToDefaults)
                        _logger.LogWarning("AudioCorrections.json 已损坏，已隔离原文件并恢复默认设备校正设置");
                    _correctionsMigrated = true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Audio correction configuration could not be loaded; writes remain disabled."); }
                AppSettings.OutputMode = audio.OutputMode;
                AppSettings.DeviceName = string.IsNullOrEmpty(audio.DeviceFriendlyName) ? ToolUtils.GetString("DefaultDevice") : audio.DeviceFriendlyName;
                AppSettings.BassOutputDeviceId = audio.BassOutputDeviceId;
                AppSettings.WasapiEndpointId = audio.WasapiEndpointId;
                AppSettings.BassASIODeviceId = audio.BassASIODeviceId;
                AppViewModel.DefaultEntryComboBoxTag = settings.DefaultEntry;
                AppViewModel.DefaultPlayListComboBoxTag = settings.DefaultPlayList;
                AppViewModel.Latency = audio.Latency;
                AppViewModel.BackdropType = settings.AppStyle;
                AppViewModel.ThemeType = settings.AppTheme;
                AppViewModel.IsRunningBackend = settings.IsRunningBackend;
                AppSettings.AutoHideDesktopLyricsOnPlayingDetail = settings.AutoHideDesktopLyricsOnPlayingDetail;
                AppSettings.IsDesktopLyricsEnabled = settings.IsDesktopLyricsEnabled;
                AppSettings.IsDesktopLyricsLocked = settings.IsDesktopLyricsLocked;
                AppSettings.IsDesktopLyricsKaraokeEnabled = settings.IsDesktopLyricsKaraokeEnabled;
                AppSettings.DesktopLyricsFontSize = settings.DesktopLyricsFontSize;
                AppSettings.DesktopLyricsFontFamily = settings.DesktopLyricsFontFamily;
                AppSettings.DesktopLyricsColorRgb = settings.DesktopLyricsColorRgb;
                AppSettings.IsDesktopLyricsCustomColorEnabled = settings.IsDesktopLyricsCustomColorEnabled;
                AppSettings.IsDesktopLyricsTranslationEnabled = settings.IsDesktopLyricsTranslationEnabled;
                AppSettings.DesktopLyricsFontWeight = settings.DesktopLyricsFontWeight;
                AppSettings.LyricsFontWeight = settings.LyricsFontWeight;
                AppViewModel.IsAutoLyricsEnabled = settings.IsAutoLyricsEnabled;
                AppViewModel.IsAutoCoverEnabled = settings.IsAutoCoverEnabled;
                AppViewModel.DsdGain = audio.DsdGain;
                AppViewModel.DsdPcmFreq = audio.DsdPcmFreq;
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
                AppViewModel.FontFamily = AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(new FontFamily(settings.GlobalFont).Source))
                    // 找不到已保存字体（被卸载/名称不匹配）时必须兜底：FontFamily 为 null 会让
                    // SaveCurrentSettings 取 GlobalFont 时抛 NRE，导致整个会话所有保存静默失败
                    ?? AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault();
                AppViewModel.DesktopLyricsFontSize = settings.DesktopLyricsFontSize;
                AppViewModel.DesktopLyricsFontFamily = AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(new FontFamily(settings.DesktopLyricsFontFamily).Source))
                    ?? AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault();
                AppViewModel.DesktopLyricsColor = Color.FromArgb(0xFF,
                    (byte)((settings.DesktopLyricsColorRgb >> 16) & 0xFF),
                    (byte)((settings.DesktopLyricsColorRgb >> 8) & 0xFF),
                    (byte)(settings.DesktopLyricsColorRgb & 0xFF));
                AppViewModel.IsDesktopLyricsCustomColorEnabled = settings.IsDesktopLyricsCustomColorEnabled;
                AppViewModel.IsDesktopLyricsKaraokeEnabled = settings.IsDesktopLyricsKaraokeEnabled;
                AppViewModel.IsDesktopLyricsTranslationEnabled = settings.IsDesktopLyricsTranslationEnabled;
                AppViewModel.IsDesktopLyricsGlowEnabled = settings.IsDesktopLyricsGlowEnabled;
                AppViewModel.IsDesktopLyricsCharFloatEnabled = settings.IsDesktopLyricsCharFloatEnabled;
                AppViewModel.IsDesktopLyricsCharScaleEnabled = settings.IsDesktopLyricsCharScaleEnabled;
                AppViewModel.DesktopLyricsLongSyllableThreshold = settings.DesktopLyricsLongSyllableThreshold;
                AppViewModel.DesktopLyricsGlowAmount = settings.DesktopLyricsGlowAmount;
                AppViewModel.DesktopLyricsCharFloatAmount = settings.DesktopLyricsCharFloatAmount;
                AppViewModel.DesktopLyricsCharScaleAmount = settings.DesktopLyricsCharScaleAmount;
                AppViewModel.DesktopLyricsShadowAmount = settings.DesktopLyricsShadowAmount;
                AppViewModel.DesktopLyricsFontWeight = settings.DesktopLyricsFontWeight;
                AppViewModel.LyricsFontWeight = settings.LyricsFontWeight;
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
                AppViewModel.IsDopEnabled = audio.IsDopEnabled;
                AppViewModel.ExperimentalSurround51 = audio.ExperimentalSurround51;
                AppViewModel.ExperimentalAtmosPassthrough = audio.ExperimentalAtmosPassthrough;
                AppViewModel.AtmosEndpointId = audio.AtmosEndpointId ?? "";
                AppViewModel.IsFadeEnabled = audio.IsFadeEnabled;
                AppViewModel.LyricsBlurAmount = settings.LyricsBlurAmount;
                AppViewModel.UseImageDominantTheme = settings.UseImageDominantTheme;
                AppViewModel.EnableLightWave = settings.EnableLightWave;
                AppViewModel.PaletteAlgorithm = (AnimatedWin2dControls.Impressionist.PaletteAlgorithm)settings.PaletteAlgorithm;
                AppViewModel.IsWin2dAnimatedText = settings.IsWin2dAnimatedText;
                AppViewModel.IsHoverScrollEnabled = settings.IsHoverScrollEnabled;
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
                AppViewModel.ToggleDesktopLyricsShortcut = settings.ToggleDesktopLyricsShortcut;
                AppViewModel.ToggleDesktopLyricsLockShortcut = settings.ToggleDesktopLyricsLockShortcut;
                AppViewModel.ToggleDesktopLyricsKaraokeShortcut = settings.ToggleDesktopLyricsKaraokeShortcut;
                AppViewModel.ResetDesktopLyricsShortcut = settings.ResetDesktopLyricsShortcut;
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
                if ((_audioSettingsMigrated && settings.HasLegacyAudioPreferences())
                    || (_correctionsMigrated && settings.DeviceCorrections != null))
                {
                    var migrated = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(settings,
                        SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
                    if (_audioSettingsMigrated) migrated.ClearLegacyAudioPreferences();
                    if (_correctionsMigrated) migrated.DeviceCorrections = null;
                    await WriteSettingsToJson(migrated);
                }
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

        private Task? _lastSettingsWrite;
        private Task _observedSettingsWrite = Task.CompletedTask;
        private Func<Task>? _saveOnUi;
        private Func<Task>? _saveStrictOnUi;
        private Task SaveOnUi() => SaveSettingAsync();
        private Task SaveStrictOnUi() => SaveSettingAsync(throwOnError: true);

        public Task SaveSettingAsync(bool throwOnError = false)
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
                return dispatcher.EnqueueAsync(throwOnError
                    ? _saveStrictOnUi ??= SaveStrictOnUi
                    : _saveOnUi ??= SaveOnUi);
            _settingsSaveQueue ??= new SettingsSaveQueue(WriteCurrentSettingsAsync);
            var pending = _settingsSaveQueue.RequestAsync();
            // 同一写入批次共用异常观察任务，不让每次滑块变化挂起一个 async 状态机。
            if (!ReferenceEquals(pending, _lastSettingsWrite))
            {
                _lastSettingsWrite = pending;
                _observedSettingsWrite = ObserveSettingsWriteAsync(pending);
            }
            return throwOnError ? pending : _observedSettingsWrite;
        }

        private async Task ObserveSettingsWriteAsync(Task pending)
        {
            try { await pending; }
            catch (Exception ex) { _logger.LogError(ex, "保存设置失败"); }
        }

        /// <summary>包含尚未触发的样式防抖值，在释放 UI 状态前等待最终写盘。</summary>
        public Task FlushSettingsAsync() => SaveSettingAsync(throwOnError: true);

        private async Task WriteCurrentSettingsAsync()
        {
            // 以最后一次已知的磁盘状态为基底合并写盘：未被当前快照适配器覆盖的字段
            // 不会被默认值冲掉；快照构建的异常也不再静默吞掉（此前 fire-and-forget 会丢掉整次保存）。
            SaveSettings baseline = _currentSettings ?? await GetSettings();
            var capture = _settingsCapture ?? throw new InvalidOperationException("Settings capture is not attached.");
            SaveSettings merged = capture.CaptureGeneral(baseline, _audioSettingsMigrated, _correctionsMigrated);
            var audio = capture.CaptureAudio();
            // 在 UI 调用顺序上排队，再派发后台任务，防止线程池调度反转保存顺序。
            await _settingsSaveGate.WaitAsync();
            try
            {
                if (_audioSettingsMigrated)
                    await Task.Run(() => _audioSettingsStore!.SaveAsync(audio));
                await WriteSettingsToJson(merged);
            }
            finally { _settingsSaveGate.Release(); }
        }


        public async Task SaveEqualizerSettingAsync()
        {
            SaveEqualizer equalizerSettings = await GetEqualizer();
            // 必须取 AppSettings.EqualizerStr（当前状态），传入旧行值会导致增益永远写不进去
            SaveEqualizer newEqualizer = SaveEqualizeSettings(new SaveEqualizer());
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

        public async Task RemoveMusic(int musicId)
        {
            try
            {
                await _dbConnection.DeleteAsync<Music>(musicId);
                AppViewModel.SongsSource.Clear();
                AppViewModel.SongsSource.AddRange(await _dbConnection.Table<Music>().ToListAsync());
                AppViewModel.NotifySongsSourceChanged();
                var usbMusicGroups = App.Services.GetRequiredService<UsbDeviceService>().MusicOnDevice.AsValueEnumerable()
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

                await WritePlayStateToJson(playState);
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
            _playStateIoGate.Wait();
            try
            {
                string json = JsonSerializer.Serialize(_currentPlayState, PlayStateJsonContext.Default.SavePlayState);
                File.WriteAllText(PlayStatePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WritePlayStateJsonSync 写入播放状态 JSON 时出错: {ex.Message}");
            }
            finally
            {
                _playStateIoGate.Release();
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

        /// <summary>
        /// 转换产物主动入库：转换流程写完文件与标签后（仍持有写入门时）立即调用。
        /// 直接走入库核心，不经过写入门检查——门正是转换流程自己持有的，
        /// 若走扫描路径的门检查会把自己挡掉，导致产物永远不入库。
        /// 已存在同路径记录时为幂等空操作。
        /// </summary>
        public async Task AddConvertedFileAsync(string path)
        {
            await _rescanfolderSemaphore.WaitAsync();
            try
            {
                var result = await AddMusicFileCoreAsync(path);
                if (result.Music is not null)
                {
                    await CommitScanBatchAsync([result], null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AddConvertedFileAsync 转换产物入库失败: {path}: {ex.Message}");
            }
            finally
            {
                _rescanfolderSemaphore.Release();
            }
        }

        /// <summary>入库核心：存在性检查 + 读取元数据，不含信号量与写入门逻辑。</summary>
        private async Task<(Music? Music, string Lyrics)> AddMusicFileCoreAsync(string path)
        {
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
                _logger.LogError(ex, $"AddMusicFileCoreAsync 添加新音乐文件时出错: {ex.Message}");
                return (null, "");
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
            string path = VersionRecordPath;
            if (!File.Exists(path))
                return null;
            await _versionRecordIoGate.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(path);
                var record = JsonSerializer.Deserialize(json, VersionJsonContext.Default.VersionRecord);
                return record?.Version;
            }
            catch (JsonException ex)
            {
                // 损坏文件留底后按未记录处理：最坏情况只是更新日志弹窗多弹一次
                _logger.LogError(ex, $"VersionRecord.json 解析失败，隔离损坏文件后按未记录继续: {ex.Message}");
                TryPreserveCorruptFile(path);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetRecordedVersionAsync 读取版本记录时出错: {ex.Message}");
                return null;
            }
            finally
            {
                _versionRecordIoGate.Release();
            }
        }

        public async Task SaveCurrentVersionAsync(string version)
        {
            await _versionRecordIoGate.WaitAsync();
            try
            {
                var record = new VersionRecord { Version = version };
                string json = JsonSerializer.Serialize(record, VersionJsonContext.Default.VersionRecord);
                await File.WriteAllTextAsync(VersionRecordPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SaveCurrentVersionAsync 保存版本记录时出错: {ex.Message}");
            }
            finally
            {
                _versionRecordIoGate.Release();
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
