using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService
    {
        private static SQLiteAsyncConnection _dbConnection;
        private static readonly string DbPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        private static AddFolderService addFolderService = new AddFolderService();

        public static async Task Initialize()
        {
            if (_dbConnection == null)
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

        public static SQLiteAsyncConnection GetDbConnection()
        {
            return _dbConnection;
        }

        public static async Task SavePlayList(List<Music> currentPlayingList)
        {
            await _dbConnection.DeleteAllAsync<LastPlayListState>();
            var musicIds = string.Join(",", currentPlayingList.Select(m => m.Id));

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
            if (playListState == null)
            {
                return new List<Music>();
            }

            var musicIds = playListState.PlayListMusicIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                         .Select(int.Parse).ToList();
            var musicList = new List<Music>();
            foreach (var musicId in musicIds)
            {
                var music = await _dbConnection.Table<Music>().Where(m => m.Id == musicId).FirstOrDefaultAsync();
                if (music != null)
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
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite error: {ex.Message}");
                return new List<Folder>();
            }
        }

        public static async Task<List<PlayList>> GetPlayListAsync()
        {
            try
            {
                return await _dbConnection.Table<PlayList>().ToListAsync();
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite error: {ex.Message}");
                return new List<PlayList>();
            }
        }

        public static Music GetMusic(int musicId)
        {
            try
            {
                //return await _dbConnection.Table<Music>().Where(m => m.Id == musicId).FirstOrDefaultAsync();
                return AppData.allSongs.Where(m => m.Id == musicId).FirstOrDefault();
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
                return null;
            }
        }

        public static async Task UpdateMusicInfo(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }

        public static List<Music> GetMusicByPlayListIdFromMem(int playListId, string search = null)
        {
            var query = from plm in AppData.allPlayListMusics
                        join m in AppData.allSongs on plm.MusicId equals m.Id
                        where plm.PlayListId == playListId
                        orderby plm.Order descending
                        select new Music
                        {
                            Id = m.Id,
                            Path = m.Path,
                            Title = m.Title,
                            Cover = m.Cover,
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
                            PlayListOrder = plm.Order
                        };

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return query.ToList();
        }

        public static async Task<List<Music>> GetMusicByPlayListId(int playListId, string search = null)
        {
            var query = from plm in await _dbConnection.Table<PlayListMusic>().ToListAsync()
                        join m in await _dbConnection.Table<Music>().ToListAsync() on plm.MusicId equals m.Id
                        where plm.PlayListId == playListId
                        orderby plm.Order descending
                        select new Music
                        {
                            Id = m.Id,
                            Path = m.Path,
                            Title = m.Title,
                            Cover = m.Cover,
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
                            PlayListOrder = plm.Order
                        };

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return query.ToList();
        }

        public static async Task UpdatePlayListMusicOrder(int playListId, Music music)
        {
            var playListMusic = await _dbConnection.Table<PlayListMusic>()
                   .Where(plm => plm.PlayListId == playListId && plm.MusicId == music.Id)
                   .FirstOrDefaultAsync();

            if (playListMusic != null)
            {
                playListMusic.Order = music.PlayListOrder;
                await _dbConnection.UpdateAsync(playListMusic);
            }
        }

        public static List<Music> FindMusicListByArtist(string artist)
        {
            //var query = from m in await _dbConnection.Table<Music>().ToListAsync()
            //            where m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
            //            select m;
            var query = from m in AppData.allSongs
                        where m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
                        select m;
            return query.ToList();
        }

        public static List<Music> FindMusicListByAlbum(string album)
        {
            //var query = from m in await _dbConnection.Table<Music>().ToListAsync()
            //            where m.Album != null && m.Album.ToLower().Equals(album.ToLower())
            //            select m;
            var query = from m in AppData.allSongs
                        where m.Album != null && m.Album.ToLower().Equals(album.ToLower())
                        select m;
            return query.ToList();
        }

        public static List<Music> FindMusicListByLastLevelFolderPath(string lastLevelFolderPath)
        {
            //var query = from m in await _dbConnection.Table<Music>().ToListAsync()
            //            where m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(lastLevelFolderPath.ToLower())
            //            select m;
            var query = from m in AppData.allSongs
                        where m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(lastLevelFolderPath.ToLower())
                        select m;
            return query.ToList();
        }

        public static async Task AddMusicListToFavour(List<Music> musics)
        {
            var maxOrder = await GetMaxOrder();
            foreach (var music in musics)
            {
                var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Id == music.Id && m.IsFavorite == true).FirstOrDefaultAsync();
                if (existingMusic != null)
                {
                    continue; // 如果已经是收藏音乐，则跳过
                }
                music.IsFavorite = true;
                music.Order = maxOrder + 1;
                await _dbConnection.UpdateAsync(music);
            }
        }

        public static async Task AddMusicListToPlayList(List<Music> musics, int playListId)
        {
            PlayListMusic lastplayListMusic = await _dbConnection.Table<PlayListMusic>()
                                          .Where(m => m.PlayListId == playListId)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            var maxOrder = 0;
            if (lastplayListMusic != null)
            {
                maxOrder = lastplayListMusic.Order;
            }
            foreach (var music in musics)
            {
                var existingRecord = await _dbConnection.Table<PlayListMusic>()
                   .Where(plm => plm.PlayListId == playListId && plm.MusicId == music.Id)
                   .FirstOrDefaultAsync();
                if (existingRecord != null)
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
            if (existingRecord == null)
            {
                PlayListMusic lastplayListMusic = await _dbConnection.Table<PlayListMusic>()
                                          .Where(m => m.PlayListId == playListId)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
                var maxOrder = 0;
                if (lastplayListMusic != null)
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
            if (lastFavouriteMusic != null)
            {
                maxOrder = lastFavouriteMusic.Order;
            }
            else
            {
                maxOrder = 1;
            }
            return maxOrder;
        }


        public static async Task RemoveMusicFromPlayList(int playListId, int musicId)
        {
            var playListMusic = await _dbConnection.Table<PlayListMusic>()
                .Where(plm => plm.PlayListId == playListId && plm.MusicId == musicId)
                .FirstOrDefaultAsync();

            if (playListMusic != null)
            {
                await _dbConnection.DeleteAsync(playListMusic);
            }
        }
        public static async Task InsertPlayList(PlayList playList)
        {
            await _dbConnection.InsertAsync(playList);
        }

        public static async Task UpdatePlayList(PlayList playList)
        {
            await _dbConnection.UpdateAsync(playList);
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

        public static async Task UpdateMuisc(Music music)
        {
            await _dbConnection.UpdateAsync(music);
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

        public static async Task<List<Music>> GetMusicListAsync()
        {
            var query = _dbConnection.Table<Music>();
            var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            Parallel.ForEach(musicList, music =>
            {
                if (music.Album == "未知专辑"
                    || music.Album == "Unknown Album"
                    || music.Album == "Álbum desconocido"
                    || music.Album == "不明なアルバム"
                    || music.Album == "Неизвестный альбом")
                {
                    music.Album = ToolUtils.GetString("UnknownAlbum");
                }
                if (music.Author == "未知艺术家"
                    || music.Author == "Unknown Artist"
                    || music.Author == "Artista desconocido"
                    || music.Author == "不明なアーティスト"
                    || music.Author == "Неизвестный артист")
                {
                    music.Author = ToolUtils.GetString("UnknownArtist");
                }
            });
            return musicList;
        }

        public static List<Music> GetMusicListFromMem(string search)
        {
            return AppData.allSongs.Where(m =>
                m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                m.Album != null && m.Album.ToLower().Contains(search.ToLower())
            ).OrderBy(m => m.Title).ToList();
        }

        public static List<Music> GetMusicListFromMemWithFolderSearchOption(string search)
        {
            return AppData.allSongs.Where(m =>
                m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
                m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Contains(search.ToLower())
            ).OrderBy(m => m.Title).ToList();
        }

        public static async Task<List<Music>> GetFavoriteMusicAsync(string search = null)
        {
            var query = _dbConnection.Table<Music>().Where(m => m.IsFavorite == true);
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            return await query.OrderByDescending(m => m.Order).ToListAsync();
        }

        public static List<Music> GetFavoriteMusicFromMem(string search = null)
        {
            var query = AppData.allSongs.Where(m => m.IsFavorite == true);
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            return query.OrderByDescending(m => m.Order).ToList();
        }

        public static List<Music> GetArtistMusicFromMem(string artist, string search = null)
        {
            var query = AppData.allSongs.AsQueryable();
            if (artist != null)
            {
                query = query.Where(m => m.Author != null && m.Author.ToLower().Equals(artist.ToLower()));
            }
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            return query.OrderBy(m => m.Album).ToList();
        }

        public static List<Music> GetFolderMusicFromMem(string folder, string search = null)
        {
            var query = AppData.allSongs.AsQueryable();
            if (folder != null)
            {
                query = query.Where(m => m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower()));
            }
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return query.OrderBy(m => m.LastLevelFolderPath).ToList();
        }

        public static List<Music> GetAlbumMusicFromMem(string album, string search = null)
        {
            var query = AppData.allSongs.AsQueryable();
            if (album != null)
            {
                query = query.Where(m => m.Album != null && m.Album.ToLower().Equals(album.ToLower()));
            }
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return query.OrderBy(m => m.TrackNumber).ToList();
        }


        public static async Task GetPlayStateAsync()
        {
            var playState = await _dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
            if (playState == null)
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
        }

        public static async Task GetSettingsAsync()
        {
            AppSettings.outputDeviceList.Clear();
            var settings = await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings != null)
            {
                AppSettings.DefualtEntry = settings.DefualtEntry;
                AppSettings.DefualtPlayList = settings.DefualtPlayList;
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                AppSettings.DeviceName = settings.DeviceFriendlyName;
                AppSettings.LrcAPISource = string.IsNullOrEmpty(settings.LrcAPISource) ? "https://api.lrc.cx" : settings.LrcAPISource;
                AppSettings.LrcAPIAuth = settings.LrcAPIAuth;
                AppSettings.AppStyle = settings.AppStyle;
                AppSettings.AppTheme = settings.AppTheme;
                AppSettings.isCoverCacheEnabled = settings.isCoverCacheEnabled;
                AppSettings.maxCoverPreLoadNum = settings.maxCoverPreLoadNum;
                AppSettings.isRunningBackend = settings.isRunningBackend;
                AppSettings.isAutoLyricsEnabled = settings.isAutoLyricsEnabled;
                AppSettings.dsdGain = settings.dsdGain;
                AppSettings.IsEqualizerEnabled = settings.IsEqualizerEnabled;
                AppSettings.equalizer = ToolUtils.ConvertToDictionary(settings.equalizerStr);
                AppSettings.EqualizerPreset = settings.EqualizerPreset;
                AppSettings.CoverSize = settings.CoverSize;
                AppSettings.EntranceAnimationTime = settings.EntranceAnimationTime;
                AppSettings.SlideAnimationTime = settings.SlideAnimationTime;
                AppSettings.DrillInAnimationTime = settings.DrillInAnimationTime;
                AppSettings.IsProcessAboveNormal = settings.IsProcessAboveNormal;
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
            newSettings.maxCoverPreLoadNum = AppSettings.maxCoverPreLoadNum;
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
            newSettings.IsProcessAboveNormal = AppSettings.IsProcessAboveNormal;
            if (settings == null)
            {
                await MusicDatabaseService.InsertSettings(newSettings);
            }
            else
            {
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
            await _dbConnection.DeleteAsync<Music>(musicId);
            AppData.allSongs = await _dbConnection.Table<Music>().ToListAsync();
            //HashSet<string> usbMusicTitles = new HashSet<string>(AppData.musicOnUsbDevice.Select(u => u.Title));
            //foreach (var music in AppData.allSongs)
            //{
            //    music.IsExistOnDevice = usbMusicTitles.Contains(music.Title);
            //}
            var usbMusicGroups = AppData.musicOnUsbDevice
                .GroupBy(u => u.Title)
                .ToDictionary(g => g.Key, g => g.ToList());
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
        public static async Task AddToFavourite(Music music, Music currentPlayingMusic)
        {
            Music lastFavouriteMusic = await _dbConnection.Table<Music>()
                                          .Where(m => m.IsFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            if (lastFavouriteMusic != null)
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

        public static async Task SavePlayState(List<Music> currentPlayingList, PlayMode currentPlayMode, int? currentPlayingMusicId, float volume)
        {
            try
            {
                await _dbConnection.DeleteAllAsync<LastPlayListState>();
                var musicIds = string.Join(",", currentPlayingList.Select(m => m.Id));

                var playListState = new LastPlayListState
                {
                    PlayListMusicIds = musicIds
                };
                _ = _dbConnection.InsertAsync(playListState);
                var playState = await _dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
                if (playState == null)
                {
                    playState = new SavePlayState
                    {
                        Id = 1
                    };
                }
                playState.PlayMode = currentPlayMode;
                playState.LastPlayedMusicId = currentPlayingMusicId;
                playState.Volume = volume;
                if (playState.Id == 0)
                {
                    _ = _dbConnection.InsertAsync(playState);
                }
                else
                {
                    _ = _dbConnection.UpdateAsync(playState);
                }
            }
            catch (Exception ex) { 
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
            var newMusicFiles = musicFiles
                .Where(m => !existingMusicPaths.Contains(m.Path))
                .ToList();

            // 只插入新的音乐文件
            if (newMusicFiles.Any())
            {
                await _dbConnection.InsertAllAsync(newMusicFiles);
            }
        }

        public static async Task RemoveFolder(int folderId)
        {
            var folderToRemove = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRemove != null)
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
            bool folderAlreadyExists = existingFolders.Any(f =>
                folder.Path.StartsWith(f.Path) || f.Path.StartsWith(folder.Path));

            if (!folderAlreadyExists)
            {
                // 移除被新文件夹包含的旧文件夹
                var foldersToRemove = existingFolders
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

        private async static Task updateMusic(Music music, string folderPath)
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(music.Path);
            var existingMusic = AppData.allSongs.Where(m => m.Path == music.Path).FirstOrDefault();
            Music newMusic = await addFolderService.getMusicInfo(storageFile, folderPath);
            existingMusic.Title = newMusic.Title;
            existingMusic.Author = newMusic.Author;
            existingMusic.Duration = newMusic.Duration;
            existingMusic.Album = newMusic.Album;
            existingMusic.FolderPath = newMusic.FolderPath;
            existingMusic.LastLevelFolderPath = newMusic.LastLevelFolderPath;
            existingMusic.BitDepth = newMusic.BitDepth;
            existingMusic.BitRate = newMusic.BitRate;
            existingMusic.SampleRate = newMusic.SampleRate;
            existingMusic.Channel = newMusic.Channel;
            if (string.IsNullOrEmpty(existingMusic.Lyrics)) {
                existingMusic.Lyrics = newMusic.Lyrics;
            }
            existingMusic.TrackNumber = newMusic.TrackNumber;
            await _dbConnection.UpdateAsync(existingMusic);
        }

        public static async Task RescanFolder(int folderId)
        {
            var folderToRescan = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRescan != null)
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
            DateTime startTime = DateTime.Now;
            // 获取StorageFolder对象
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<StorageFile> files = null;
            List<Music> musicFilesInFolder = null;
            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files = new List<StorageFile>();
                files.AddRange(currentFiles);
                musicFilesInFolder = AppData.allSongs
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

            // 优化1: 并行处理文件路径收集
            await Task.Run(() =>
            {
                Parallel.ForEach(files, file =>
                {
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
                });
            });

            // 存储需要删除的 Music 项
            var toDelete = new ConcurrentBag<Music>();
            var toUpdate = new ConcurrentBag<Music>();

            // 优化2: 并行检查现有音乐文件
            await Task.Run(() =>
            {
                Parallel.ForEach(musicFilesInFolder, newMusic =>
                {
                    if (!filePaths.ContainsKey(newMusic.Path))
                    {
                        toDelete.Add(newMusic);
                    }
                    else
                    {
                        toUpdate.Add(newMusic);
                        filePaths.TryRemove(newMusic.Path, out _);
                    }
                });
            });

            // 优化3: 并行执行删除操作
            var deleteTasks = toDelete.Select(async music =>
            {
                await _dbConnection.DeleteAsync(music);
                musicFilesInFolder.Remove(music);
            });
            await Task.WhenAll(deleteTasks);

            // 优化4: 并行执行更新操作
            var updateTasks = toUpdate.Select(async music =>
            {
                try
                {
                    await updateMusic(music, folder.Path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新音乐文件时出错: {ex.Message}");
                }
            });
            await Task.WhenAll(updateTasks);

            // 优化5: 并行执行添加操作
            var addTasks = filePaths.Keys.Select(async path =>
            {
                try
                {
                    var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == path).FirstOrDefaultAsync();
                    if (existingMusic != null)
                    {
                        return;
                    }
                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                    Music music = await addFolderService.getMusicInfo(storageFile, folder.Path);
                    if (music != null)
                    {
                        await _dbConnection.InsertAsync(music);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"添加新音乐文件时出错: {ex.Message}");
                }
            });
            await Task.WhenAll(addTasks);

            // 更新UI和主窗口的音乐列表
            if (isUpdate)
            {
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
                {
                    mainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        mainWindow.UpdateMusicList();
                    });
                }
            }

            //// 获取StorageFolder对象
            //var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            //List<StorageFile> files = null;
            //List<Music> musicFilesInFolder = null;
            //if (isSingleFolder)
            //{
            //    var currentFiles = await folder.GetFilesAsync();
            //    files = new List<StorageFile>();
            //    files.AddRange(currentFiles);
            //    musicFilesInFolder = AppData.allSongs
            //        .Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            //}
            //else
            //{
            //    files = await GetAllFilesInFolderAndSubfolders(folder);
            //    musicFilesInFolder = await _dbConnection.Table<Music>()
            //       .Where(m => m.FolderPath.Contains(folderPath))
            //       .ToListAsync();
            //}
            //HashSet<string> filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            //// 遍历 IReadOnlyList<StorageFile>，将文件路径添加到 HashSet 中
            //foreach (var file in files)
            //{
            //    try
            //    {
            //        if (ToolUtils.IsMusicFile(file.FileType))
            //        {
            //            filePaths.Add(file.Path);
            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        System.Diagnostics.Debug.WriteLine($"添加文件路径时出错: {ex.Message}");
            //    }
            //}

            //// 存储需要删除的 Music 项
            //var toDelete = new List<Music>();

            //// 检查 Music 列表中的项
            //foreach (var newMusic in musicFilesInFolder)
            //{
            //    if (!filePaths.Contains(newMusic.Path))
            //    {
            //        toDelete.Add(newMusic);
            //    }
            //    else
            //    {
            //        await updateMusic(newMusic, folder.Path);
            //        filePaths.Remove(newMusic.Path);
            //    }
            //}

            //// 执行删除操作
            //foreach (var music in toDelete)
            //{
            //    await _dbConnection.DeleteAsync(music);
            //    musicFilesInFolder.Remove(music);
            //}

            //// 执行添加操作
            //foreach (var path in filePaths)
            //{
            //    var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == path).FirstOrDefaultAsync();
            //    if (existingMusic != null)
            //    {
            //        continue;
            //    }
            //    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
            //    Music music = await addFolderService.getMusicInfo(storageFile, folder.Path);
            //    await _dbConnection.InsertAsync(music);
            //}
            //// 更新UI和主窗口的音乐列表
            //if (isUpdate)
            //{
            //    var mainWindow = (App.MainWindow as MainWindow);
            //    if (mainWindow != null)
            //    {
            //        mainWindow.DispatcherQueue.TryEnqueue(() =>
            //        {
            //            mainWindow.UpdateMusicList();
            //        });
            //    }
            //}


            Debug.WriteLine($"重新扫描文件夹耗时: {(DateTime.Now - startTime).TotalSeconds}秒");
        }


        public static async Task<List<UsbDeviceMusic>> GetUsbDeviceMusics(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public static async Task<List<UsbDeviceMusic>> RescanUsbDeviceFolderByPath(List<UsbDeviceMusic> usbDeviceMusics, string uniqueDeviceId, string folderPath, bool isSingleFolder = false)
        {
            // 获取StorageFolder对象
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<StorageFile> files = null;
            List<UsbDeviceMusic> musicFilesInFolder = null;
            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files = new List<StorageFile>();
                files.AddRange(currentFiles);
                musicFilesInFolder = usbDeviceMusics.Where(m => Path.GetDirectoryName(m.Path) == folderPath).ToList();
            }
            else
            {
                files = await GetAllFilesInFolderAndSubfolders(folder);
                musicFilesInFolder = usbDeviceMusics
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
                    //StorageFile storageFile = await StorageFile.GetFileFromPathAsync(newMusic.Path);
                    //UsbDeviceMusic existingMusic = usbDeviceMusics.Where(m => m.Path == newMusic.Path).FirstOrDefault();
                    //Music music = await addFolderService.getMusicInfo(storageFile, folderPath);
                    //existingMusic.Title = music.Title;
                    //existingMusic.Author = music.Author;
                    //existingMusic.Extension = music.Extension;
                    //existingMusic.Album = music.Album;                    
                    //await _dbConnection.UpdateAsync(existingMusic);
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
                var existingMusic = usbDeviceMusics.Where(m => m.Path == path).FirstOrDefault();
                if (existingMusic != null)
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
