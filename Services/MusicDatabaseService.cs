using Microsoft.UI.Xaml.Media;
using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService
    {
        private static SQLiteAsyncConnection _dbConnection;
        private static string DbPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        private static AddFolderService addFolderService = new AddFolderService();

        private static SemaphoreSlim _rescanfolderSemaphore = new SemaphoreSlim(4, 4);
        private static ConcurrentBag<Music> _toDelete = [];
        private static ConcurrentBag<Music> _toUpdate = [];
        private static List<StorageFile> _files = [];
        private static List<Music> _musicFilesInFolder = null;
        private static readonly object lockObject = new object();
        public static async Task Initialize()
        {
            InitalizeDbPath();
            if (_dbConnection is null)
            {
                _dbConnection = new SQLiteAsyncConnection(DbPath);
                await _dbConnection.CreateTableAsync<Music>();
                await _dbConnection.CreateTableAsync<Folder>();
                await _dbConnection.CreateTableAsync<SavePlayState>();
                await _dbConnection.CreateTableAsync<SaveSettings>();
                await _dbConnection.CreateTableAsync<PlayList>();
                await _dbConnection.CreateTableAsync<PlayListMusic>();
                await _dbConnection.CreateTableAsync<LastPlayListState>();
                await _dbConnection.CreateTableAsync<SubFolder>();
                await _dbConnection.CreateTableAsync<UsbDeviceMusic>();
                await _dbConnection.CreateTableAsync<UsbDeviceSubFolder>();
            }
        }

        private static void InitalizeDbPath()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (userProfilePath is not null)
                {
                    // 拼接应用文件夹路径，这里假设应用文件夹名为"MyAppFolder"
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
            catch (Exception)
            {
                DbPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            }
        }

        private static async void CopyFile(string sourceFilePath, string targetFilePath)
        {

            if (File.Exists(sourceFilePath))
            {

                using (FileStream sourceStream = File.Open(sourceFilePath, FileMode.Open))
                {
                    using (FileStream destinationStream = File.Create(targetFilePath))
                    {
                        await sourceStream.CopyToAsync(destinationStream);
                    }
                }
            }
        }

        public static SQLiteAsyncConnection GetDbConnection()
        {
            return _dbConnection;
        }

        public static async Task SavePlayList(List<Music> currentPlayingList)
        {
            await _dbConnection.DeleteAllAsync<LastPlayListState>();
            var musicIds = string.Join(",", currentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());

            var playListState = new LastPlayListState
            {
                PlayListMusicIds = musicIds
            };
            await _dbConnection.InsertAsync(playListState);
        }

        public static async Task InsertSubFolders(List<SubFolder> subFolder)
        {
            await _dbConnection.InsertAllAsync(subFolder);
        }

        public static async Task InsertUsbDeviceSubFolders(List<UsbDeviceSubFolder> usbDeviceSubFolders)
        {
            await _dbConnection.InsertAllAsync(usbDeviceSubFolders);
        }

        public static async Task AddSubFolder(SubFolder subFolder)
        {
            await _dbConnection.InsertAsync(subFolder);
        }

        public static async Task AddUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
        {
            await _dbConnection.InsertAsync(subFolder);
        }

        public static async Task UpdateSubFolder(SubFolder subFolder)
        {
            await _dbConnection.UpdateAsync(subFolder);
        }

        public static async Task UpdateUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
        {
            await _dbConnection.UpdateAsync(subFolder);
        }

        public static async Task DeleteSubFolder(SubFolder subFolder)
        {
            await _dbConnection.DeleteAsync(subFolder);
        }

        public static async Task DeleteUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
        {
            await _dbConnection.DeleteAsync(subFolder);
        }

        public static async Task DeleteSubFolderByPath(string subFolderPath)
        {
            var musicToDelete = await _dbConnection.Table<Music>()
                                              .Where(m => m.Path.Contains(subFolderPath))
                                              .ToListAsync();
            foreach (var music in musicToDelete)
            {
                await _dbConnection.DeleteAsync(music);
            }
        }

        public static async Task DeleteUsbDeviceSubFolderByPath(string subFolderPath, string uniqueDeviceId)
        {
            List<UsbDeviceMusic> musicToDelete = await _dbConnection.Table<UsbDeviceMusic>()
                                              .Where(m => m.Path.Contains(subFolderPath) && m.UniqueDeviceId == uniqueDeviceId)
                                              .ToListAsync();
            foreach (var music in musicToDelete)
            {
                await _dbConnection.DeleteAsync(music);
            }
        }

        public static async Task DeleteAllSubFolder()
        {
            await _dbConnection.DeleteAllAsync<SubFolder>();
        }

        public static async Task<List<SubFolder>> GetSubFolders(int folderId)
        {
            return await _dbConnection.Table<SubFolder>().Where(f => f.FolderId == folderId).ToListAsync();
        }

        public static async Task<List<UsbDeviceSubFolder>> GetUsbDeviceSubFolders(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceSubFolder>().Where(f => f.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public static async Task<List<Folder>> GetFolders()
        {
            return await _dbConnection.Table<Folder>().ToListAsync();
        }

        public static async Task<List<Music>> LoadPlayList()
        {
            var playListState = await _dbConnection.Table<LastPlayListState>().FirstOrDefaultAsync();
            if (playListState is null)
            {
                return new List<Music>();
            }

            var musicIds = playListState.PlayListMusicIds.Split(',', StringSplitOptions.RemoveEmptyEntries).AsValueEnumerable()
                                                         .Select(int.Parse).ToList();
            var musicList = new List<Music>();
            foreach (var musicId in musicIds)
            {
                var music = await _dbConnection.Table<Music>().Where(m => m.Id == musicId).FirstOrDefaultAsync();
                if (music is not null)
                {
                    musicList.Add(music);
                }
            }

            return musicList;
        }

        public static async Task<List<Folder>> GetFoldersAsync()
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

        public static async Task<List<PlayList>> GetPlayListAsync()
        {
            try
            {
                return await _dbConnection.Table<PlayList>().ToListAsync();
            }
            catch (SQLiteException)
            {
                return new List<PlayList>();
            }
        }

        public static Music GetMusic(int musicId)
        {
            try
            {
                return AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == musicId);
            }
            catch (SQLiteException)
            {
                return null;
            }
        }

        public static async Task UpdateMusicInfo(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }

        public static IEnumerable<Music> GetMusicByPlayListIdFromMem(int playListId, string search = null)
        {
            var query = AppData.allPlayListMusics
                        .AsValueEnumerable()
                        .Where(plm => plm.PlayListId == playListId)
                        .Join(
                            AppData.allSongs.AsValueEnumerable(),
                            plm => plm.MusicId,
                            m => m.Id,
                            (plm, m) => new Music
                            {
                                Id = m.Id,
                                Path = m.Path,
                                Title = m.Title,
                                Author = m.Author,
                                Duration = m.Duration,
                                Album = m.Album,
                                FolderPath = m.FolderPath,
                                LastLevelFolderPath = m.LastLevelFolderPath,
                                Extension = m.Extension,
                                Order = m.Order,
                                BitDepth = m.BitDepth,
                                BitRate = m.BitRate,
                                SampleRate = m.SampleRate,
                                IsFavorite = m.IsFavorite,
                                TrackNumber = m.TrackNumber,
                                Lyrics = m.Lyrics,
                                PlayListOrder = plm.Order,
                                CreateTime = m.CreateTime,
                                UpdateTime = m.UpdateTime,
                            }
                        )
                        .OrderByDescending(m => m.PlayListOrder);

            if (!string.IsNullOrEmpty(search))
            {
                return query.Where(m =>
                     m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                     m.Album is not null && m.Album.ToLower().Contains(search.ToLower()) ||
                     m.Author is not null && m.Author.ToLower().Contains(search.ToLower())
                 ).ToImmutableList();
            }
            return query.ToImmutableList();
        }
        public static async Task UpdatePlayListMusicOrderBatch(int playListId, IEnumerable<Music> musicList)
        {
            try
            {
                // 批量查询所有相关的 PlayListMusic 记录
                var musicIds = musicList.AsValueEnumerable().Select(m => m.Id).ToList();
                var playListMusics = await _dbConnection.Table<PlayListMusic>()
                    .Where(plm => plm.PlayListId == playListId && musicIds.Contains(plm.MusicId))
                    .ToListAsync();

                // 创建字典以便快速查找
                var musicOrderDict = musicList.AsValueEnumerable().ToDictionary(m => m.Id, m => m.PlayListOrder);

                // 更新 Order 字段
                foreach (var plm in playListMusics)
                {
                    if (musicOrderDict.TryGetValue(plm.MusicId, out var newOrder))
                    {
                        plm.Order = newOrder;
                    }
                }

                if (playListMusics.Count != 0)
                {
                    await _dbConnection.UpdateAllAsync(playListMusics);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"批量更新播放列表音乐排序时出错: {ex.Message}");
            }
        }

        public static IEnumerable<Music> FindMusicListByArtist(string artist)
        {
            return AppData.allSongs.AsValueEnumerable()
                   .Where(m => m.Author is not null && m.Author.ToLower().Equals(artist.ToLower())).ToImmutableList();
        }

        public static IEnumerable<Music> FindMusicListByAlbum(string album)
        {
            return AppData.allSongs.AsValueEnumerable()
                   .Where(m => m.Album is not null && m.Album.ToLower().Equals(album.ToLower())).OrderBy(m => m.TrackNumber).ToImmutableList();
        }

        public static IEnumerable<Music> FindMusicListByLastLevelFolderPath(string lastLevelFolderPath)
        {
            return AppData.allSongs.AsValueEnumerable()
                   .Where(m => m.LastLevelFolderPath is not null && m.LastLevelFolderPath.ToLower().Equals(lastLevelFolderPath.ToLower())).ToImmutableList();
        }

        public static async Task AddMusicListToFavour(IEnumerable<Music> musics)
        {
            var maxOrder = await GetMaxOrder();
            foreach (var music in musics)
            {
                var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Id == music.Id && m.IsFavorite == true).FirstOrDefaultAsync();
                if (existingMusic is not null)
                {
                    continue; // 如果已经是收藏音乐，则跳过
                }
                music.IsFavorite = true;
                music.Order = maxOrder + 1;
                await _dbConnection.UpdateAsync(music);
            }
        }

        public static async Task AddMusicListToPlayList(IEnumerable<Music> musics, int playListId)
        {
            PlayListMusic lastplayListMusic = await _dbConnection.Table<PlayListMusic>()
                                          .Where(m => m.PlayListId == playListId)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            var maxOrder = 0;
            if (lastplayListMusic is not null)
            {
                maxOrder = lastplayListMusic.Order;
            }
            foreach (var music in musics)
            {
                var existingRecord = await _dbConnection.Table<PlayListMusic>()
                   .Where(plm => plm.PlayListId == playListId && plm.MusicId == music.Id)
                   .FirstOrDefaultAsync();
                if (existingRecord is not null)
                {
                    continue; // 如果已经在播放列表中，则跳过
                }
                int newOrder = maxOrder + 1;
                var playListMusic = new PlayListMusic
                {
                    PlayListId = playListId,
                    MusicId = music.Id,
                    Order = newOrder
                };
                await _dbConnection.InsertAsync(playListMusic);
            }
            AppData.allSongs = await GetMusicListAsync();
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }

        public static async Task AddMusicToPlayList(int playListId, int musicId)
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
                var maxOrder = 0;
                if (lastplayListMusic is not null)
                {
                    maxOrder = lastplayListMusic.Order + 1;
                }
                int newOrder = maxOrder + 1;
                var playListMusic = new PlayListMusic
                {
                    PlayListId = playListId,
                    MusicId = musicId,
                    Order = newOrder
                };
                await _dbConnection.InsertAsync(playListMusic);
            }
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }

        private static async Task<int> GetMaxOrder()
        {
            Music lastFavouriteMusic = await _dbConnection.Table<Music>()
                                          .Where(m => m.IsFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            int maxOrder = 0;
            if (lastFavouriteMusic is not null)
            {
                maxOrder = lastFavouriteMusic.Order;
            }
            else
            {
                maxOrder = 1;
            }
            return maxOrder;
        }
        public static async Task DeleteAllMusicFromPlayList(int playListId, IEnumerable<int> musicIds)
        {
            if (musicIds is null || musicIds.AsValueEnumerable().Count() == 0)
            {
                return;
            }

            // 将 musicIds 转换为逗号分隔的字符串，用于 SQL IN 子句
            var musicIdsString = string.Join(",", musicIds);

            // 构建 SQL 删除语句
            var sql = $"DELETE FROM PlayListMusic WHERE PlayListId = ? AND MusicId IN ({musicIdsString})";

            // 执行 SQL 语句
            await _dbConnection.ExecuteAsync(sql, playListId);
        }

        public static async Task RemoveMusicFromPlayList(int playListId, int musicId)
        {
            var playListMusic = await _dbConnection.Table<PlayListMusic>()
                .Where(plm => plm.PlayListId == playListId && plm.MusicId == musicId)
                .FirstOrDefaultAsync();

            if (playListMusic is not null)
            {
                await _dbConnection.DeleteAsync(playListMusic);
            }
        }
        public static async Task<int> InsertPlayList(PlayList playList)
        {
            await _dbConnection.InsertAsync(playList);
            return playList.Id;
        }

        public static async Task UpdatePlayList(PlayList playList)
        {
            await _dbConnection.UpdateAsync(playList);
        }

        public static async Task<PlayList> GetPlayListByName(string playListName)
        {
            return await _dbConnection.Table<PlayList>()
                .Where(plm => plm.Name == playListName)
                .FirstOrDefaultAsync();
        }

        public static async Task RemovePlayList(PlayList playList)
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

        public static async Task UpdateAllAsync(List<Music> musicList)
        {
            await _dbConnection.UpdateAllAsync(musicList);
        }

        public static async Task<SaveSettings> GetSettings()
        {
            return await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
        }

        public static async Task InsertSettings(SaveSettings settings)
        {
            await _dbConnection.InsertAsync(settings);
        }

        public static async Task UpdateSettings(SaveSettings settings)
        {
            await _dbConnection.UpdateAsync(settings);
        }

        public static async Task UpdateEqualizerSettings(string equalizerStr, bool isEnabled)
        {
            await _dbConnection.ExecuteAsync(
                "UPDATE SaveSettings SET equalizerStr = ?, IsEqualizerEnabled = ? WHERE Id = 1",
                equalizerStr,
                isEnabled
            );
        }
        public static async Task GetPlayListMusic()
        {
            AppData.allPlayListMusics.Clear();
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }


        public static async Task<IReadOnlyCollection<Music>> GetMusicListAsync()
        {
            // 提前获取本地化字符串（仅调用两次，避免循环内重复调用）
            string localizedUnknownAlbum = ToolUtils.GetString("UnknownAlbum");
            string localizedUnknownArtist = ToolUtils.GetString("UnknownArtist");
            // 直接获取原始列表（避免中间变量复制）
            var musicList = await _dbConnection
                .Table<Music>()
                .OrderBy(m => m.Title)
                .ToListAsync();
            foreach (var music in musicList)
            {
                // 仅在需要时才修改，减少不必要的字符串赋值（字符串是不可变的，赋值会创建新对象）
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

        public static IEnumerable<Music> GetMusicListFromMem(string search)
        {
            return AppData.allSongs.AsValueEnumerable().Where(m =>
                m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                m.Author is not null && m.Author.ToLower().Contains(search.ToLower()) ||
                m.Album is not null && m.Album.ToLower().Contains(search.ToLower())
            ).OrderBy(m => m.Title).ToImmutableList();
        }

        public static IEnumerable<Music> GetMusicListFromMemWithFolderSearchOption(string search)
        {
            return AppData.allSongs.AsValueEnumerable().Where(m =>
                m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                m.Author is not null && m.Author.ToLower().Contains(search.ToLower()) ||
                m.Album is not null && m.Album.ToLower().Contains(search.ToLower()) ||
                m.LastLevelFolderPath is not null && m.LastLevelFolderPath.ToLower().Contains(search.ToLower())
            ).OrderBy(m => m.Title).ToImmutableList();
        }

        public static IEnumerable<Music> GetFavoriteMusicFromMem(string search = null)
        {
            if (!string.IsNullOrEmpty(search))
            {
                return AppData.allSongs.AsValueEnumerable().Where(m => m.IsFavorite == true).Where(m =>
                    m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author is not null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album is not null && m.Album.ToLower().Contains(search.ToLower())
                ).OrderByDescending(m => m.Order).ToList();
            }
            else {
                return AppData.allSongs.AsValueEnumerable().Where(m => m.IsFavorite == true).OrderByDescending(m => m.Order).ToList();
            }
        }

        public static IEnumerable<Music> GetArtistMusicFromMem(string artist, string search = null)
        {
            var query = AppData.allSongs.AsValueEnumerable();
            if (artist is not null)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    return query.Where(m =>
                        m.Author is not null && m.Author.ToLower().Equals(artist.ToLower()) ||
                        m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                        m.Album is not null && m.Album.ToLower().Contains(search.ToLower())
                    ).OrderBy(m => m.Album).ToImmutableList();
                }
                else
                {
                    return query.Where(m => m.Author is not null && m.Author.ToLower().Equals(artist.ToLower()))
                         .OrderBy(m => m.Album).ToImmutableList();
                }
            }
            return query.OrderBy(m => m.Album).ToImmutableList();
        }

        public static IEnumerable<Music> GetFolderMusicFromMem(string folder, string search = null)
        {
            var query = AppData.allSongs.AsValueEnumerable();
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

        public static IEnumerable<Music> GetAlbumMusicFromMem(string album, string search = null)
        {
            var query = AppData.allSongs.AsValueEnumerable();
            if (album is not null)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    return query.Where(m =>
                        m.Title is not null && m.Title.ToLower().Contains(search.ToLower()) ||
                        m.Author is not null && m.Author.ToLower().Contains(search.ToLower())
                    ).OrderBy(m => m.TrackNumber).ToImmutableList();
                }
                else
                {
                    return query.Where(m => m.Album is not null && m.Album.ToLower().Equals(album.ToLower()))
                         .OrderBy(m => m.TrackNumber).ToImmutableList();
                }
            }
            return query.OrderBy(m => m.TrackNumber).ToImmutableList();
        }


        public static async Task GetPlayStateAsync()
        {
            var playState = await _dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
            if (playState is null)
            {
                // 如果没有记录，默认设置为列表循环
                playState = new SavePlayState
                {
                    PlayMode = PlayMode.ListLoop,
                    Volume = 0.5f,
                    LastPlayedMusicId = null
                };
                await _dbConnection.InsertAsync(playState);
            }
            AppData.PlayMode = playState.PlayMode;
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppData.Volume = playState.Volume;
            AppData.sortOrder = playState.sortOrder;
        }

        public static async Task GetSettingsAsync()
        {
            var settings = await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings is not null)
            {
                AppSettings.DefualtEntry = settings.DefualtEntry;
                AppSettings.DefualtPlayList = settings.DefualtPlayList;
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                AppSettings.DeviceName = settings.DeviceFriendlyName;
                AppSettings.LrcAPISource = settings.LrcAPISource;
                AppSettings.LrcAPIAuth = settings.LrcAPIAuth;
                AppSettings.AppStyle = settings.AppStyle;
                AppSettings.AppTheme = settings.AppTheme;
                AppSettings.isCoverCacheEnabled = settings.isCoverCacheEnabled;
                AppSettings.isRunningBackend = settings.isRunningBackend;
                AppSettings.isAutoLyricsEnabled = settings.isAutoLyricsEnabled;
                AppSettings.dsdGain = settings.dsdGain;
                AppSettings.dsdPcmFreq = settings.dsdPcmFreq;
                AppSettings.IsEqualizerEnabled = settings.IsEqualizerEnabled;
                AppSettings.equalizerStr = settings.equalizerStr;
                AppSettings.equalizer = ToolUtils.ConvertToDictionary(settings.equalizerStr);
                AppSettings.EqualizerPreset = settings.EqualizerPreset;
                AppSettings.CoverSize = settings.CoverSize;
                AppSettings.EntranceAnimationTime = settings.EntranceAnimationTime;
                AppSettings.SlideAnimationTime = settings.SlideAnimationTime;
                AppSettings.DrillInAnimationTime = settings.DrillInAnimationTime;
                AppSettings.IsBackgroundCoverEnabled = settings.IsBackgroundCoverEnabled;
                AppSettings.IsFolderWatchEnabled = settings.IsFolderWatchEnabled;
                AppSettings.CoverLoadThreadCount = settings.CoverLoadThreadCount;
                AppSettings.IsCustomAppSize = settings.IsCustomAppSize;
                AppSettings.AppWidth = settings.AppWidth;
                AppSettings.AppHeight = settings.AppHeight;
                AppSettings.GlobalFont = new FontFamily(settings.GlobalFont);
                AppSettings.CustomAcrylicOpacity = settings.CustomAcrylicOpacity;
                AppSettings.CustomColorAlpha = settings.CustomColorAlpha;
                AppSettings.CustomColorRed = settings.CustomColorRed;
                AppSettings.CustomColorGreen = settings.CustomColorGreen;
                AppSettings.CustomColorBlue = settings.CustomColorBlue;
                AppSettings.IsUpdateBackDrop = settings.IsUpdateBackDrop;
                AppSettings.LyricsAlignment = ToolUtils.ConvertStringToTextAlignment(settings.LyricsAlignment);
                AppSettings.LyricsMargin = settings.LyricsMargin;
                AppSettings.GlobalFontSize = settings.GlobalFontSize;
                AppSettings.IsGlobalFontSizeEnabled = settings.IsGlobalFontSizeEnabled;
                AppSettings.MusicCoverCache = settings.MusicCoverCache;
                AppSettings.BassOutputDeviceId = settings.BassOutputDeviceId;
                AppSettings.IsDopEnabled = settings.IsDopEnabled;
            }
        }

        public static async Task SaveSettingAsync()
        {
            SaveSettings settings = await GetSettings();
            SaveSettings newSettings = new SaveSettings();
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.Latency = AppSettings.Latency;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
            newSettings.DefualtEntry = AppSettings.DefualtEntry;
            newSettings.DefualtPlayList = AppSettings.DefualtPlayList;
            newSettings.LrcAPISource = AppSettings.LrcAPISource;
            newSettings.LrcAPIAuth = AppSettings.LrcAPIAuth;
            newSettings.AppStyle = AppSettings.AppStyle;
            newSettings.AppTheme = AppSettings.AppTheme;
            newSettings.isCoverCacheEnabled = AppSettings.isCoverCacheEnabled;
            newSettings.isRunningBackend = AppSettings.isRunningBackend;
            newSettings.isAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
            newSettings.dsdGain = AppSettings.dsdGain;
            newSettings.equalizerStr = ToolUtils.ConvertToJson(AppSettings.equalizer);
            newSettings.IsEqualizerEnabled = AppSettings.IsEqualizerEnabled;
            newSettings.EqualizerPreset = AppSettings.EqualizerPreset;
            newSettings.CoverSize = AppSettings.CoverSize;
            newSettings.DrillInAnimationTime = AppSettings.DrillInAnimationTime;
            newSettings.EntranceAnimationTime = AppSettings.EntranceAnimationTime;
            newSettings.SlideAnimationTime = AppSettings.SlideAnimationTime;
            newSettings.IsBackgroundCoverEnabled = AppSettings.IsBackgroundCoverEnabled;
            newSettings.IsFolderWatchEnabled = AppSettings.IsFolderWatchEnabled;
            newSettings.CoverLoadThreadCount = AppSettings.CoverLoadThreadCount;
            newSettings.IsCustomAppSize = AppSettings.IsCustomAppSize;
            newSettings.AppHeight = AppSettings.AppHeight;
            newSettings.AppWidth = AppSettings.AppWidth;
            newSettings.GlobalFont = AppSettings.GlobalFont.Source;
            newSettings.CustomAcrylicOpacity = AppSettings.CustomAcrylicOpacity;
            newSettings.CustomColorAlpha = AppSettings.CustomColorAlpha;
            newSettings.CustomColorRed = AppSettings.CustomColorRed;
            newSettings.CustomColorGreen = AppSettings.CustomColorGreen;
            newSettings.CustomColorBlue = AppSettings.CustomColorBlue;
            newSettings.IsUpdateBackDrop = AppSettings.IsUpdateBackDrop;
            newSettings.LyricsAlignment = ConvertTextAlignmentToString(AppSettings.LyricsAlignment);
            newSettings.LyricsMargin = AppSettings.LyricsMargin;
            newSettings.GlobalFontSize = AppSettings.GlobalFontSize;
            newSettings.IsGlobalFontSizeEnabled = AppSettings.IsGlobalFontSizeEnabled;
            newSettings.MusicCoverCache = AppSettings.MusicCoverCache;
            newSettings.BassOutputDeviceId = AppSettings.BassOutputDeviceId;
            newSettings.IsDopEnabled = AppSettings.IsDopEnabled;
            newSettings.dsdPcmFreq = AppSettings.dsdPcmFreq;
            if (settings is null)
            {
                await MusicDatabaseService.InsertSettings(newSettings);
            }
            else
            {
                newSettings.Id = settings.Id;
                await MusicDatabaseService.UpdateSettings(newSettings);
            }
        }

        public static async Task SavePlayStateAsync(SavePlayState playState)
        {
            await _dbConnection.InsertOrReplaceAsync(playState);
        }

        public static async Task SaveSettingsAsync(SaveSettings settings)
        {
            await _dbConnection.InsertOrReplaceAsync(settings);
        }

        public static async Task RemoveMusic(int musicId)
        {
            try
            {
                await _dbConnection.DeleteAsync<Music>(musicId);
                AppData.allSongs = await _dbConnection.Table<Music>().ToListAsync();
                var usbMusicGroups = AppData.musicOnUsbDevice.AsValueEnumerable()
                    .GroupBy(u => u.Title)
                    .ToDictionary(g => g.Key, g => g.AsValueEnumerable().ToList());
                foreach (var music in AppData.allSongs)
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
                Debug.WriteLine($"删除音乐时出错: {e.Message}");
            }
        }
        public static async Task CancelMusicsFavourite(IEnumerable<Music> musics)
        {
            foreach (var music in musics)
            {
                music.IsFavorite = false;
            }
            await _dbConnection.UpdateAllAsync(musics);
        }
        public static async Task AddToFavourite(Music music, Music currentPlayingMusic)
        {
            Music lastFavouriteMusic = await _dbConnection.Table<Music>()
                                          .Where(m => m.IsFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            if (lastFavouriteMusic is not null)
            {
                if (music.IsFavorite)
                {
                    music.Order = lastFavouriteMusic.Order + 1;
                }
                else
                {
                    music.Order = 0;
                }
            }
            else
            {
                music.Order = 1;
            }
            await _dbConnection.UpdateAsync(music);
        }

        public static async Task<Music> LoadCurrentPlayingMusic(int? lastPlayedMusicId)
        {
            return await _dbConnection.Table<Music>().Where(m => m.Id == lastPlayedMusicId).FirstOrDefaultAsync();
        }

        public static async Task SavePlayState(List<Music> currentPlayingList, PlayMode currentPlayMode, int? currentPlayingMusicId, float volume, string sortOrder)
        {
            try
            {
                await _dbConnection.DeleteAllAsync<LastPlayListState>();
                var musicIds = string.Join(",", currentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());

                var playListState = new LastPlayListState
                {
                    PlayListMusicIds = musicIds
                };
                _ = _dbConnection.InsertAsync(playListState);
                var playState = await _dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
                if (playState is null)
                {
                    playState = new SavePlayState
                    {
                        Id = 1
                    };
                }
                playState.PlayMode = currentPlayMode;
                playState.LastPlayedMusicId = currentPlayingMusicId;
                playState.Volume = volume;
                playState.sortOrder = sortOrder;
                if (playState.Id == 0)
                {
                    _ = _dbConnection.InsertAsync(playState);
                }
                else
                {
                    _ = _dbConnection.UpdateAsync(playState);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存播放状态时出错: {ex.Message}");
            }
        }

        public static async Task<List<Music>> GetMusicListByFolder(StorageFolder folder)
        {
            var musicFiles = new List<Music>();
            await addFolderService.GetMusicFilesRecursive(folder, musicFiles);
            return musicFiles;
        }

        public static async Task ScanFolderAsync(StorageFolder folder, int folderId)
        {
            var musicFiles = new List<Music>();
            // 递归获取所有音乐文件
            List<SubFolder> subFolders = AutoRescanService.RecordInitialFolderTimes(folder.Path, folderId);
            await InsertSubFolders(subFolders);
            await addFolderService.GetMusicFilesRecursive(folder, musicFiles);
            // 获取已存在的音乐文件路径
            var existingMusicPaths = await _dbConnection.Table<Music>()
                .ToListAsync()
                .ContinueWith(t => t.Result.Select(m => m.Path).ToList());

            // 过滤掉已存在的音乐文件
            var newMusicFiles = musicFiles.AsValueEnumerable()
                .Where(m => !existingMusicPaths.Contains(m.Path))
                .ToList();

            // 只插入新的音乐文件
            if (newMusicFiles.AsValueEnumerable().Any())
            {
                await _dbConnection.InsertAllAsync(newMusicFiles);
            }
        }

        public static async Task RemoveFolder(int folderId)
        {
            var folderToRemove = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRemove is not null)
            {
                // 删除该文件夹及其所有子文件夹下的音乐文件
                var musicFilesToRemove = await _dbConnection.Table<Music>()
                    .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                    .ToListAsync();

                foreach (var musicFile in musicFilesToRemove)
                {
                    await _dbConnection.DeleteAsync(musicFile);
                }

                var subfoldersToRemove = await _dbConnection.Table<SubFolder>()
                    .Where(sf => sf.Path.StartsWith(folderToRemove.Path))
                    .ToListAsync();
                foreach (var subfolder in subfoldersToRemove)
                {
                    Debug.WriteLine($"删除子文件夹: {subfolder.Path},{subfolder.FolderId}");
                    await _dbConnection.DeleteAsync(subfolder);
                }

                // 移除文件夹信息
                await _dbConnection.DeleteAsync(folderToRemove);
            }
        }

        public static async Task<Folder> GetFolder(int folderId)
        {
            return await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
        }

        public static async Task CheckFolderBeforeAdd(StorageFolder folder)
        {
            var existingFolders = await _dbConnection.Table<Folder>().ToListAsync();

            // 检查新添加的文件夹是否已经在已存在的文件夹中
            bool folderAlreadyExists = existingFolders.AsValueEnumerable().Any(f =>
                folder.Path.StartsWith(f.Path) || f.Path.StartsWith(folder.Path));

            if (!folderAlreadyExists)
            {
                // 移除被新文件夹包含的旧文件夹
                var foldersToRemove = existingFolders.AsValueEnumerable()
                    .Where(f => folder.Path.StartsWith(f.Path))
                    .ToList();

                foreach (var folderToRemove in foldersToRemove)
                {
                    // 删除该文件夹及其音乐文件
                    var musicFilesToRemove = await _dbConnection.Table<Music>()
                        .Where(m => m.FolderPath.StartsWith(folderToRemove.Path))
                        .ToListAsync();

                    foreach (var musicFile in musicFilesToRemove)
                    {
                        await _dbConnection.DeleteAsync(musicFile);
                    }

                    await _dbConnection.DeleteAsync(folderToRemove);
                }

                // 存储新文件夹信息到数据库
                var newFolder = new Folder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Type = "本地"
                };
                await _dbConnection.InsertAsync(newFolder);
                // 扫描文件夹中的音乐文件
                await ScanFolderAsync(folder, newFolder.Id);

            }
        }
        public static async Task<List<StorageFile>> GetAllFilesInFolderAndSubfolders(StorageFolder folder)
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
                // 处理异常，例如权限不足等情况
                System.Diagnostics.Debug.WriteLine($"获取文件时出错: {ex.Message}");
            }

            return allFiles;
        }

        private async static Task<Music> updateMusic(Music music)
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(music.Path);
            //var existingMusic = AppData.allSongs.AsValueEnumerable().Where(m => m.Path == music.Path).FirstOrDefault();
            //if (existingMusic is null)
            //{
            //    return null;
            //}
            Music newMusic = await ToolUtils.GetMusicInfo(storageFile);
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
                if (string.IsNullOrEmpty(music.Lyrics))
                {
                    music.Lyrics = newMusic.Lyrics;
                }
                music.TrackNumber = newMusic.TrackNumber;
                music.DiskNumber = newMusic.DiskNumber;
                music.Year = newMusic.Year;
                music.UpdateTime = newMusic.UpdateTime;
                music.CreateTime = newMusic.CreateTime;
            });
            return music;

        }

        public static async Task RescanFolder(int folderId)
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
                    System.Diagnostics.Debug.WriteLine($"重新扫描文件夹时出错: {ex.Message}");
                }
            }
        }

        public static async Task RescanFolderByPath(string folderPath, bool isUpdate = true, bool isSingleFolder = false)
        {
            _toDelete.Clear();
            _toUpdate.Clear();
            _files.Clear();
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);

            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                _files.AddRange(currentFiles);
                _musicFilesInFolder = AppData.allSongs.AsValueEnumerable()
                    .Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            }
            else
            {
                _files = await GetAllFilesInFolderAndSubfolders(folder);
                _musicFilesInFolder = await _dbConnection.Table<Music>()
                   .Where(m => m.FolderPath.Contains(folderPath))
                   .ToListAsync();
            }

            var filePaths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            // 并行处理文件路径收集
            var filePathTasks = _files.AsValueEnumerable().Select(async file =>
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
                    System.Diagnostics.Debug.WriteLine($"添加文件路径时出错: {ex.Message}");
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            await Task.WhenAll(filePathTasks.ToList());
            //并行检查现有音乐文件
            var checkTasks = _musicFilesInFolder.AsValueEnumerable().Select(async newMusic =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    if (!filePaths.ContainsKey(newMusic.Path))
                    {
                        _toDelete.Add(newMusic);
                    }
                    else
                    {
                        _toUpdate.Add(newMusic);
                        filePaths.TryRemove(newMusic.Path, out _);
                    }
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });

            await Task.WhenAll(checkTasks.ToList());
            // 并行执行删除操作
            var deleteTasks = _toDelete.AsValueEnumerable().Select(async music =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    await _dbConnection.DeleteAsync(music);
                    _musicFilesInFolder.Remove(music);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"删除音乐文件时出错: {ex.Message}");
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });

            await Task.WhenAll(deleteTasks.ToList());
            //并行执行更新操作
            var updateTasks = _toUpdate.AsValueEnumerable().Select(async music =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    return await updateMusic(music);
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            }).ToArray();
            var results = await Task.WhenAll(updateTasks);
            var validResults = results.AsValueEnumerable().Where(r => r is not null).ToList();
            if (validResults.Count != 0)
            {
                await _dbConnection.UpdateAllAsync(validResults);
                Debug.WriteLine($"批量更新完成，共 {validResults.Count} 条记录");
            }

            //完全批量处理
            var addTasks = filePaths.Keys.AsValueEnumerable().Select(async path =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == path).FirstOrDefaultAsync();
                    if (existingMusic is not null)
                    {
                        return null;
                    }
                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                    Music music = await ToolUtils.GetMusicInfo(storageFile);
                    return music;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"添加新音乐文件时出错: {ex.Message}");
                    return null;
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            var AddResults = await Task.WhenAll(addTasks.ToList());
            var validMusic = AddResults.AsValueEnumerable().Where(m => m is not null).ToList();
            if (validMusic.Count != 0)
            {
                await _dbConnection.InsertAllAsync(validMusic);
            }
            if (isUpdate)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    App.MainWindow.UpdateMusicList();
                });
            }
        }

        public static async Task RescanFolderWithOutUpdateAll(string folderPath, bool isSingleFolder = false)
        {
            var toDelete = new ConcurrentBag<Music>();
            var files = new List<StorageFile>();
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<Music> musicFilesInFolder = null;
            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files.AddRange(currentFiles);
                musicFilesInFolder = AppData.allSongs.AsValueEnumerable()
                    .Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            }
            else
            {
                files = await GetAllFilesInFolderAndSubfolders(folder);
                musicFilesInFolder = await _dbConnection.Table<Music>()
                   .Where(m => m.FolderPath.Contains(folderPath))
                   .ToListAsync();
            }

            var filePaths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            // 并行处理文件路径收集
            var filePathTasks = files.AsValueEnumerable().Select(async file =>
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
                    System.Diagnostics.Debug.WriteLine($"添加文件路径时出错: {ex.Message}");
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            await Task.WhenAll(filePathTasks.ToList());
            //并行检查现有音乐文件
            var checkTasks = musicFilesInFolder.AsValueEnumerable().Select(async newMusic =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    if (!filePaths.ContainsKey(newMusic.Path))
                    {
                        toDelete.Add(newMusic);
                    }
                    else
                    {
                        filePaths.TryRemove(newMusic.Path, out _);
                    }
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });

            await Task.WhenAll(checkTasks.ToList());
            // 并行执行删除操作
            var deleteTasks = toDelete.AsValueEnumerable().Select(async music =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    await _dbConnection.DeleteAsync(music);
                    musicFilesInFolder.Remove(music);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"删除音乐文件时出错: {ex.Message}");
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });

            await Task.WhenAll(deleteTasks.ToList());
            //完全批量处理
            var addTasks = filePaths.Keys.AsValueEnumerable().Select(async path =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == path).FirstOrDefaultAsync();
                    if (existingMusic is not null)
                    {
                        return null;
                    }

                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                    Music music = await ToolUtils.GetMusicInfo(storageFile);
                    return music;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"添加新音乐文件时出错: {ex.Message}");
                    return null;
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            var AddResults = await Task.WhenAll(addTasks.ToList());
            var validMusic = AddResults.AsValueEnumerable().Where(m => m is not null).ToList();
            if (validMusic.Count != 0)
            {
                await _dbConnection.InsertAllAsync(validMusic);
            }
        }

        public static async Task AddMusicList(IEnumerable<Music> _toAdd)
        {
            var addTasks = _toAdd.AsValueEnumerable().Select(async m =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(m.Path);
                    Music music = await ToolUtils.GetMusicInfo(storageFile);
                    return music;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"添加新音乐文件时出错: {ex.Message}");
                    return null;
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            var AddResults = await Task.WhenAll(addTasks.ToList());
            var validMusic = AddResults.AsValueEnumerable().Where(m => m is not null).ToList();
            if (validMusic.Count != 0)
            {
                await _dbConnection.InsertAllAsync(validMusic);
            }
        }

        public static async Task UpdateMusicList(IEnumerable<Music> _toUpdate) {
            //并行执行更新操作
            var updateTasks = _toUpdate.AsValueEnumerable().Select(async music =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    return await updateMusic(music);
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            }).ToArray();
            var results = await Task.WhenAll(updateTasks);
            var validResults = results.AsValueEnumerable().Where(r => r is not null).ToList();
            if (validResults.Count != 0)
            {
                await _dbConnection.UpdateAllAsync(validResults);
                Debug.WriteLine($"批量更新完成，共 {validResults.Count} 条记录");
            }
        }

        public static async Task DeletedMusicList(IEnumerable<Music> toDelete)
        {
            // 并行执行删除操作
            var deleteTasks = toDelete.AsValueEnumerable().Select(async music =>
            {
                await _rescanfolderSemaphore.WaitAsync();
                try
                {
                    await _dbConnection.DeleteAsync(music);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"删除音乐文件时出错: {ex.Message}");
                }
                finally
                {
                    _rescanfolderSemaphore.Release();
                }
            });
            await Task.WhenAll(deleteTasks.ToList());
        }

        public static async Task<List<UsbDeviceMusic>> GetUsbDeviceMusics(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public static async Task<List<UsbDeviceMusic>> RescanUsbDeviceFolderByPath(List<UsbDeviceMusic> usbDeviceMusics, string uniqueDeviceId, string folderPath, bool isSingleFolder = false)
        {
            // 获取StorageFolder对象
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<StorageFile> files = [];
            List<UsbDeviceMusic> musicFilesInFolder = null;
            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files = new List<StorageFile>();
                files.AddRange(currentFiles);
                musicFilesInFolder = usbDeviceMusics.AsValueEnumerable().Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            }
            else
            {
                files = await GetAllFilesInFolderAndSubfolders(folder);
                musicFilesInFolder = usbDeviceMusics.AsValueEnumerable()
                   .Where(m => m.Path.Contains(folderPath)).ToList();
            }
            HashSet<string> filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 遍历 IReadOnlyList<StorageFile>，将文件路径添加到 HashSet 中
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
                    System.Diagnostics.Debug.WriteLine($"添加文件路径时出错: {ex.Message}");
                }
            }

            // 存储需要删除的 Music 项
            var toDelete = new List<UsbDeviceMusic>();

            // 检查 Music 列表中的项
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

            // 执行删除操作
            foreach (var music in toDelete)
            {
                await _dbConnection.DeleteAsync(music);
                musicFilesInFolder.Remove(music);
            }

            // 执行添加操作
            List<UsbDeviceMusic> usbDeviceMusicsInsertList = new List<UsbDeviceMusic>();
            foreach (var path in filePaths)
            {
                var existingMusic = usbDeviceMusics.AsValueEnumerable().Where(m => m.Path == path).FirstOrDefault();
                if (existingMusic is not null)
                {
                    continue;
                }
                StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                UsbDeviceMusic usbDeviceMusic = addFolderService.getUsbDeviceMusicInfo(storageFile, folder.Path, uniqueDeviceId);
                usbDeviceMusicsInsertList.Add(usbDeviceMusic);
                await _dbConnection.InsertAsync(usbDeviceMusic);
            }
            return usbDeviceMusicsInsertList;
        }
    }
}
