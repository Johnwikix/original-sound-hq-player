using Microsoft.UI.Xaml.Shapes;
using SQLite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
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
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
                return new List<PlayList>();
            }
        }

        public static async Task<Music> GetMusic(int musicId)
        {
            try
            {
                return await _dbConnection.Table<Music>().Where(m => m.Id == musicId).FirstOrDefaultAsync();
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
                            //Channel = m.Channel,
                            isFavorite = m.isFavorite,
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
                            //Channel = m.Channel,
                            isFavorite = m.isFavorite,
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

        public static async Task<List<Music>> FindMusicListByArtist(string artist)
        {
            var query = from m in await _dbConnection.Table<Music>().ToListAsync()
                        where m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
                        select m;
            return query.ToList();
        }

        public static async Task<List<Music>> FindMusicListByAlbum(string album)
        {
            var query = from m in await _dbConnection.Table<Music>().ToListAsync()
                        where m.Album != null && m.Album.ToLower().Equals(album.ToLower())
                        select m;
            return query.ToList();
        }

        public static async Task<List<Music>> FindMusicListByLastLevelFolderPath(string lastLevelFolderPath)
        {
            var query = from m in await _dbConnection.Table<Music>().ToListAsync()
                        where m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(lastLevelFolderPath.ToLower())
                        select m;
            return query.ToList();
        }

        public static async Task AddMusicListToFavour(List<Music> musics)
        {
            var maxOrder = await GetMaxOrder();
            foreach (var music in musics)
            {
                var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Id == music.Id && m.isFavorite == true).FirstOrDefaultAsync();
                if (existingMusic != null)
                {
                    continue; // 如果已经是收藏音乐，则跳过
                }
                music.isFavorite = true;
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
                                          .Where(m => m.isFavorite)
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
        public static async Task GetPlayListMusic()
        {
            AppData.allPlayListMusics.Clear();
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }

        public static async Task<List<Music>> GetMusicListAsync()
        {
            var query = _dbConnection.Table<Music>();
            //if (!string.IsNullOrEmpty(search))
            //{
            //    query = query.Where(m =>
            //        m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //        m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
            //        m.Album != null && m.Album.ToLower().Contains(search.ToLower())
            //    );
            //}
            return await query.OrderBy(m => m.Title).ToListAsync();
        }

        public static List<Music> GetMusicListFromMem(string search) {
            return AppData.allSongs.Where(m =>
                m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                m.Album != null && m.Album.ToLower().Contains(search.ToLower())
            ).OrderBy(m => m.Title).ToList();
        }

        public static async Task<List<Music>> GetFavoriteMusicAsync(string search = null)
        {
            var query = _dbConnection.Table<Music>().Where(m => m.isFavorite == true);
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
            var query = AppData.allSongs.Where(m => m.isFavorite == true);
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

        //public static async Task<List<Music>> GetArtistMusicAsync(string artist, string search = null)
        //{
        //    var query = _dbConnection.Table<Music>().Where(m => m.Author != null && m.Author.ToLower().Equals(artist.ToLower()));
        //    if (!string.IsNullOrEmpty(search))
        //    {
        //        query = query.Where(m =>
        //            m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
        //            m.Album != null && m.Album.ToLower().Contains(search.ToLower())
        //        );
        //    }
        //    return await query.OrderBy(m => m.Album).ToListAsync();
        //}

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

        //public static async Task<List<Music>> GetFolderMusicAsync(string folder, string search = null)
        //{
        //    var query = _dbConnection.Table<Music>().Where(m => m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower()));
        //    if (!string.IsNullOrEmpty(search))
        //    {
        //        query = query.Where(m =>
        //            m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
        //            m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
        //            m.Author != null && m.Author.ToLower().Contains(search.ToLower())
        //        );
        //    }
        //    return await query.OrderBy(m => m.LastLevelFolderPath).ToListAsync();
        //}

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

        //public static async Task<List<Music>> GetAlbumMusicAsync(string album, string search = null)
        //{
        //    var query = _dbConnection.Table<Music>().Where(m => m.Album != null && m.Album.ToLower().Equals(album.ToLower()));
        //    if (!string.IsNullOrEmpty(search))
        //    {
        //        query = query.Where(m =>
        //            m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
        //            m.Author != null && m.Author.ToLower().Contains(search.ToLower())
        //        );
        //    }
        //    return await query.OrderBy(m => m.TrackNumber).ToListAsync();
        //}

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


        public static async Task<SavePlayState> GetPlayStateAsync()
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
            return playState;
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
                AppSettings.LrcAPISource = settings.LrcAPISource;
                AppSettings.LrcAPIAuth = settings.LrcAPIAuth;
                AppSettings.AppStyle = settings.AppStyle;
                AppSettings.AppTheme = settings.AppTheme;
                AppSettings.isCoverCacheEnabled = settings.isCoverCacheEnabled;
                AppSettings.maxCoverPreLoadNum = settings.maxCoverPreLoadNum;
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
        }
        public static async Task AddToFavourite(Music music, Music currentPlayingMusic)
        {
            Music lastFavouriteMusic = await _dbConnection.Table<Music>()
                                          .Where(m => m.isFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            if (lastFavouriteMusic != null)
            {
                if (music.isFavorite)
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

        public static async Task SavePlayState(List<Music> currentPlayingList,PlayMode currentPlayMode, int? currentPlayingMusicId, float volume)
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

        public static async Task ScanFolderAsync(StorageFolder folder)
        {
            var musicFiles = new List<Music>();
            // 递归获取所有音乐文件
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

                // 移除文件夹信息
                await _dbConnection.DeleteAsync(folderToRemove);
            }
        }

        public static async Task<List<Folder>> GetFolders()
        {
            return await _dbConnection.Table<Folder>().ToListAsync();
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
                await ScanFolderAsync(folder);

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

        private async static Task updateMusic(Music music,string folderPath) {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(music.Path);
            var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == music.Path).FirstOrDefaultAsync();
            Music newMusic = await addFolderService.getMusicInfo(storageFile, folderPath);
            existingMusic.Title = newMusic.Title;
            existingMusic.Author = newMusic.Author;
            existingMusic.Duration = newMusic.Duration;
            existingMusic.Album = newMusic.Album;
            //existingMusic.Extension = newMusic.Extension;
            existingMusic.BitDepth = newMusic.BitDepth;
            existingMusic.BitRate = newMusic.BitRate;
            existingMusic.SampleRate = newMusic.SampleRate;
            existingMusic.Channel = newMusic.Channel;
            existingMusic.Lyrics = newMusic.Lyrics;
            existingMusic.TrackNumber = newMusic.TrackNumber;
            await _dbConnection.UpdateAsync(existingMusic);
        }

        public static async Task RescanLastLevelFolder(string folderPath) {
            List<Music> musics = await _dbConnection.Table<Music>().Where(f => f.FolderPath == folderPath).ToListAsync();
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<StorageFile> files = await GetAllFilesInFolderAndSubfolders(folder);
            List<string> filePaths = files.Select(file => file.Path).ToList();
            List<string> musicPaths = musics.Select(music => music.Path).ToList();
            var filesNotInMusics = filePaths.Except(musicPaths).ToList();
            var musicsNotInFiles = musicPaths.Except(filePaths).ToList();
            if (musicsNotInFiles.Count > 0) {
                foreach (var music in musicsNotInFiles)
                {
                    var musicToDelete = await _dbConnection.Table<Music>().Where(m => m.Path == music).FirstOrDefaultAsync();
                    if (musicToDelete != null)
                    {
                        await _dbConnection.DeleteAsync(musicToDelete);
                    }
                    musics.Remove(musicToDelete);
                }
            }
            if (filesNotInMusics.Count > 0) {
                foreach (var file in filesNotInMusics)
                {
                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(file);
                    Music music = await addFolderService.getMusicInfo(storageFile, folderPath);
                    await _dbConnection.InsertAsync(music);
                }
            }
            HashSet<string> pathSet = new HashSet<string>(musicsNotInFiles);

            // 过滤Music列表，排除Path存在于pathSet中的项
            List<Music> filteredList = musics
                .Where(m => !pathSet.Contains(m.Path))
                .ToList();            
            foreach (Music music in filteredList) {
                if (IsMusicFile(music.Extension)) {
                    await updateMusic(music, music.FolderPath);
                }                
            }
           
        }

        public static async Task RescanFolder(int folderId)
        {
            var folderToRescan = await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
            if (folderToRescan != null)
            {
                try
                {
                    // 获取StorageFolder对象

                    var folder = await StorageFolder.GetFolderFromPathAsync(folderToRescan.Path);
                    List<StorageFile> files = await GetAllFilesInFolderAndSubfolders(folder);

                    var musicFilesInFolder = await _dbConnection.Table<Music>()
                       .Where(m => m.FolderPath.Contains(folderToRescan.Path))
                       .ToListAsync();

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
                    var toDelete = new List<Music>();

                    // 检查 Music 列表中的项
                    foreach (var newMusic in musicFilesInFolder)
                    {
                        if (!filePaths.Contains(newMusic.Path))
                        {
                            toDelete.Add(newMusic);
                        }
                        else
                        {
                            await updateMusic(newMusic, folder.Path);
                            //StorageFile storageFile = await StorageFile.GetFileFromPathAsync(newMusic.Path);
                            //var existingMusic = await _dbConnection.Table<Music>().Where(m => m.Path == newMusic.Path).FirstOrDefaultAsync();
                            //Music music = await addFolderService.getMusicInfo(storageFile, folder.Path);
                            //existingMusic.Title = music.Title;
                            //existingMusic.Author = music.Author;
                            //existingMusic.Duration = music.Duration;
                            //existingMusic.Album = music.Album;
                            //existingMusic.Extension = newMusic.Extension;
                            //existingMusic.BitDepth = music.BitDepth;
                            //existingMusic.BitRate = music.BitRate;
                            //existingMusic.SampleRate = music.SampleRate;
                            //existingMusic.Channel = music.Channel;
                            //existingMusic.Lyrics = music.Lyrics;
                            //existingMusic.TrackNumber = music.TrackNumber;
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
                    foreach (var path in filePaths)
                    {
                        StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
                        Music music = await addFolderService.getMusicInfo(storageFile, folder.Path);
                        await _dbConnection.InsertAsync(music);
                    }
                    // 更新UI和主窗口的音乐列表
                    var mainWindow = (App.MainWindow as MainWindow);
                    if (mainWindow != null)
                    {
                        mainWindow.UpdateMusicList();
                        //await mainWindow.LoadMusicList();
                        //await mainWindow.LoadFavourMusicList();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"重新扫描文件夹时出错: {ex.Message}");
                }
            }
        }
    }
}
