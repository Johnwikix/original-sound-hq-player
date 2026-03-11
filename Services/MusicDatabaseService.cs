using ATL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playlists;
using Windows.Storage;
using Windows.UI;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService
    {
        private SQLiteAsyncConnection _dbConnection;
        private string DbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        private readonly AddFolderService addFolderService = new();
        private readonly SemaphoreSlim _rescanfolderSemaphore = new(4, 4);
        private readonly ConcurrentBag<Music> _toDelete = [];
        private readonly ConcurrentBag<Music> _toUpdate = [];
        private List<StorageFile> _files = [];
        private List<Music> _musicFilesInFolder = null;
        private AppViewModel AppViewModel { get; set; }

        public async Task Initialize()
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
            AppViewModel = App.Services.GetRequiredService<AppViewModel>();
            InitalizeSettings();
        }

        private void InitalizeDbPath()
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

        private async void InitalizeSettings() {
            SaveSettings settings = await GetSettings();            
            if (settings is null)
            {
                SaveSettings newSettings = SaveCurrentSettings(new SaveSettings());
                await InsertSettings(newSettings);
            }
        }

        private async void CopyFile(string sourceFilePath, string targetFilePath)
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

        public SQLiteAsyncConnection GetDbConnection()
        {
            return _dbConnection;
        }

        public async Task SavePlayList(List<Music> currentPlayingList)
        {
            await _dbConnection.DeleteAllAsync<LastPlayListState>();
            var musicIds = string.Join(",", currentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());

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

        public async Task InsertUsbDeviceSubFolders(List<UsbDeviceSubFolder> usbDeviceSubFolders)
        {
            await _dbConnection.InsertAllAsync(usbDeviceSubFolders);
        }

        public async Task AddSubFolder(SubFolder subFolder)
        {
            await _dbConnection.InsertAsync(subFolder);
        }

        public async Task AddUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
        {
            await _dbConnection.InsertAsync(subFolder);
        }

        public async Task UpdateSubFolder(SubFolder subFolder)
        {
            await _dbConnection.UpdateAsync(subFolder);
        }

        public async Task UpdateUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
        {
            await _dbConnection.UpdateAsync(subFolder);
        }

        public async Task DeleteSubFolder(SubFolder subFolder)
        {
            await _dbConnection.DeleteAsync(subFolder);
        }

        public async Task DeleteUsbDeviceSubFolder(UsbDeviceSubFolder subFolder)
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

        public async Task DeleteUsbDeviceSubFolderByPath(string subFolderPath, string uniqueDeviceId)
        {
            List<UsbDeviceMusic> musicToDelete = await _dbConnection.Table<UsbDeviceMusic>()
                                              .Where(m => m.Path.Contains(subFolderPath) && m.UniqueDeviceId == uniqueDeviceId)
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

        public async Task<List<UsbDeviceSubFolder>> GetUsbDeviceSubFolders(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceSubFolder>().Where(f => f.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public async Task<List<Folder>> GetFolders()
        {
            return await _dbConnection.Table<Folder>().ToListAsync();
        }

        public async Task<List<Music>> LoadPlayList()
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
                //foreach (var playList in list) {
                //    AppViewModel.AllPlayList.Add(playList);
                //}
            }
            catch
            {
            }
        }

        public async Task UpdateMusicInfo(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }
        public IEnumerable<PlayListMusicItem> GetMusicByPlayListIdFromMem(int playListId, string search = null)
        {
            var query = AppData.allPlayListMusics
                .Where(plm => plm.PlayListId == playListId)
                .Join(
                    AppViewModel.AllSongs,
                    plm => plm.MusicId,
                    m => m.Id,
                    (plm, m) => new PlayListMusicItem
                    {
                        Music = m,           // 引用指向 AllSongs 中的对象
                        PlayListOrder = plm.Order // 歌单特有顺序
                    }
                )
                .OrderByDescending(vm => vm.PlayListOrder);

            if (!string.IsNullOrEmpty(search))
            {
                return query.Where(vm =>
                    (vm.Music.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (vm.Music.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (vm.Music.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }

            return query;
        }

        public async Task UpdatePlayListMusicOrderBatch(int playListId, IEnumerable<PlayListMusicItem> musicList)
        {
            try
            {
                // 批量查询所有相关的 PlayListMusic 记录
                var musicIds = musicList.AsValueEnumerable().Select(m => m.Music.Id).ToList();
                var playListMusics = await _dbConnection.Table<PlayListMusic>()
                    .Where(plm => plm.PlayListId == playListId && musicIds.Contains(plm.MusicId))
                    .ToListAsync();

                // 创建字典以便快速查找
                var musicOrderDict = musicList.AsValueEnumerable().ToDictionary(m => m.Music.Id, m => m.PlayListOrder);

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

        public IEnumerable<Music> FindMusicListByAlbum(string album)
        {
            return AppViewModel.AllSongs.AsValueEnumerable()
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
                    continue; // 如果已经是收藏音乐，则跳过
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
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
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

        private async Task<int> GetMaxOrder()
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
        public async Task DeleteAllMusicFromPlayList(int playListId, IEnumerable<int> musicIds)
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

        public async Task RemoveMusicFromPlayList(int playListId, int musicId)
        {
            var playListMusic = await _dbConnection.Table<PlayListMusic>()
                .Where(plm => plm.PlayListId == playListId && plm.MusicId == musicId)
                .FirstOrDefaultAsync();

            if (playListMusic is not null)
            {
                await _dbConnection.DeleteAsync(playListMusic);
            }
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

        public async Task UpdateAllAsync(List<Music> musicList)
        {
            await _dbConnection.UpdateAllAsync(musicList);
        }

        public async Task<SaveSettings> GetSettings()
        {
            return await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
        }

        public async Task InsertSettings(SaveSettings settings)
        {
            await _dbConnection.InsertAsync(settings);
        }

        public async Task UpdateSettings(SaveSettings settings)
        {
            await _dbConnection.UpdateAsync(settings);
        }

        public async Task UpdateEqualizerSettings(string equalizerStr, bool isEnabled)
        {
            await _dbConnection.ExecuteAsync(
                "UPDATE SaveSettings SET equalizerStr = ?, IsEqualizerEnabled = ? WHERE Id = 1",
                equalizerStr,
                isEnabled
            );
        }
        public async Task GetPlayListMusic()
        {
            AppData.allPlayListMusics.Clear();
            AppData.allPlayListMusics = await _dbConnection.Table<PlayListMusic>().ToListAsync();
        }

        public async Task LoadMusicList() {
            await AppViewModel.AllSongs.AddRangeAsync(await GetMusicListAsync());
            await InitalPlayListAsync();
            await GetPlayListMusic();
            AppViewModel.SelectedSortOption = AppViewModel.SortOptions.AsValueEnumerable().FirstOrDefault(item => item.Tag == AppData.SortOrder) 
                ?? AppViewModel.SortOptions.AsValueEnumerable().FirstOrDefault() ?? new SortOption("DefaultOrder", "SortOrderDefault");
        }


        public async Task<IReadOnlyCollection<Music>> GetMusicListAsync()
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

        public ObservableCollection<Music> GetFavoriteMusicFromMem(string search = null)
        {
            return new(AppViewModel.AllSongs.Where(m => m.IsFavorite == true).OrderByDescending(m => m.Order));
        }

        public IEnumerable<Music> GetArtistMusicFromMem(string artist, string search = null)
        {
            var query = AppViewModel.AllSongs.AsValueEnumerable();
            if (artist is not null)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    return query.Where(m => m.Author is not null && m.Author.ToLower().Equals(artist.ToLower()))
                        .Where(m =>
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

        public IEnumerable<Music> GetFolderMusicFromMem(string folder, string search = null)
        {
            var query = AppViewModel.AllSongs.AsValueEnumerable();
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
            var playState = await _dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
            if (playState is null)
            {
                // 如果没有记录，默认设置为列表循环
                playState = new SavePlayState
                {
                    PlayMode = PlayMode.ListLoop,
                    Volume = 50f,
                    LastPlayedMusicId = null
                };
                await _dbConnection.InsertAsync(playState);
            }
            AppViewModel.CurrentPlayMode = playState.PlayMode;
            AppViewModel.PlayModeFlyoutText = ToolUtils.GetPlayModeText(playState.PlayMode);
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppViewModel.Volume = playState.Volume;
            AppViewModel.TempVolume = playState.Volume;
            AppData.SortOrder = playState.sortOrder;
        }

        public async Task GetSettingsAsync()
        {
            var settings = await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings is not null)
            {
                AppViewModel.DefaultEntryComboBoxTag = settings.DefualtEntry;
                AppViewModel.DefaultPlayListComboBoxTag = settings.DefualtPlayList;
                AppSettings.OutputMode = settings.OutputMode;
                AppViewModel.Latency = settings.Latency;
                AppSettings.DeviceName = settings.DeviceFriendlyName;
                AppViewModel.BackdropType = settings.AppStyle;
                AppViewModel.ThemeType = settings.AppTheme;
                AppViewModel.IsCoverCacheEnabled = settings.isCoverCacheEnabled;
                AppViewModel.IsRunningBackend = settings.isRunningBackend;
                AppViewModel.IsAutoLyricsEnabled = settings.isAutoLyricsEnabled;
                AppViewModel.DsdGain = settings.dsdGain;
                AppSettings.dsdPcmFreq = settings.dsdPcmFreq;
                AppSettings.IsEqualizerEnabled = settings.IsEqualizerEnabled;
                AppSettings.equalizerStr = settings.equalizerStr;
                AppSettings.equalizer = ToolUtils.ConvertToDictionary(settings.equalizerStr);
                AppSettings.EqualizerPreset = settings.EqualizerPreset;
                AppViewModel.CoverSize = settings.CoverSize;
                AppViewModel.EntranceAnimationTime = settings.EntranceAnimationTime;
                AppViewModel.SlideAnimationTime = settings.SlideAnimationTime;
                AppViewModel.DrillInAnimationTime = settings.DrillInAnimationTime;
                AppViewModel.IsBackgroundCoverEnabled = settings.IsBackgroundCoverEnabled;
                AppViewModel.IsFolderWatchEnabled = settings.IsFolderWatchEnabled;
                AppSettings.IsCustomAppSize = settings.IsCustomAppSize;
                AppSettings.AppWidth = settings.AppWidth;
                AppSettings.AppHeight = settings.AppHeight;
                AppSettings.GlobalFont = new FontFamily(settings.GlobalFont);
                AppViewModel.CustomOpacity = settings.CustomAcrylicOpacity * 100;
                AppViewModel.CustomColor = Color.FromArgb(settings.CustomColorAlpha,
                                                  settings.CustomColorRed,
                                                 settings.CustomColorGreen,
                                                 settings.CustomColorBlue);
                AppSettings.IsUpdateBackDrop = settings.IsUpdateBackDrop;
                AppSettings.LyricsAlignment = ToolUtils.ConvertStringToTextAlignment(settings.LyricsAlignment);
                AppViewModel.LyricsMargin = new Thickness(settings.LyricsMargin, 0, settings.LyricsMargin, 0);
                AppSettings.GlobalFontSize = settings.GlobalFontSize;
                AppSettings.IsGlobalFontSizeEnabled = settings.IsGlobalFontSizeEnabled;
                AppSettings.MusicCoverCache = settings.MusicCoverCache;
                AppSettings.BassOutputDeviceId = settings.BassOutputDeviceId;
                AppSettings.IsDopEnabled = settings.IsDopEnabled;
                AppViewModel.IsPlayDetailButtonVisible = settings.IsPlayDetailBtnVisible;
                AppSettings.IsFadeEnabled = settings.IsFadeEnabled;
                AppSettings.IsWFWLyrics = settings.IsWFWLyrics;
                AppSettings.LyricsBlurAmount = Math.Clamp(settings.LyricsBlurAmount, 0, 1000);
                LoadSettingsToAppViewModel();
            }
        }

        private void LoadSettingsToAppViewModel() {            
            //AppViewModel.DsdGain = AppSettings.dsdGain;
            //AppViewModel.IsAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
            //AppViewModel.IsRunningBackend = AppSettings.isRunningBackend;
            //AppViewModel.Latency = AppSettings.Latency;
            //AppViewModel.DefaultEntryComboBoxTag = AppSettings.DefualtEntry;
            //AppViewModel.DefaultPlayListComboBoxTag = AppSettings.DefualtPlayList;
            //AppViewModel.BackdropType = AppSettings.AppStyle;
            if (AppViewModel.BackdropType != "CustomAcrylicStyle")
            {
                AppViewModel.IsColorPickerVisible = false;
            }
            else
            {
                AppViewModel.IsColorPickerVisible = true;
            }
            //AppViewModel.CustomOpacity = AppSettings.CustomAcrylicOpacity * 100;
            //AppViewModel.CustomColor = Color.FromArgb(AppSettings.CustomColorAlpha,
            //                                     AppSettings.CustomColorRed,
            //                                     AppSettings.CustomColorGreen,
            //                                     AppSettings.CustomColorBlue);
            //AppViewModel.ThemeType = AppSettings.AppTheme;
            //AppViewModel.EntranceAnimationTime = AppSettings.EntranceAnimationTime;
            //AppViewModel.SlideAnimationTime = AppSettings.SlideAnimationTime;
            //AppViewModel.DrillInAnimationTime = AppSettings.DrillInAnimationTime;
            //AppViewModel.IsFolderWatchEnabled = AppSettings.IsFolderWatchEnabled;
            AppViewModel.IsCustomAppSize = AppSettings.IsCustomAppSize;
            AppViewModel.AppHeight = AppSettings.AppHeight;
            AppViewModel.AppWidth = AppSettings.AppWidth;
            AppViewModel.Version = $"{Windows.ApplicationModel.Package.Current.Id.Version.Major}.{Windows.ApplicationModel.Package.Current.Id.Version.Minor}.{Windows.ApplicationModel.Package.Current.Id.Version.Build}.{Windows.ApplicationModel.Package.Current.Id.Version.Revision}";
            AppViewModel.FontFamilyList = new ObservableCollection<FontInfo>(ToolUtils.GetSystemFontsInternal());
            AppViewModel.FontFamily = AppViewModel.FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(AppSettings.GlobalFont.Source));
            AppViewModel.IsDopEnabled = AppSettings.IsDopEnabled;
            AppViewModel.IsFadeEnabled = AppSettings.IsFadeEnabled;
            AppViewModel.IsUpdateBackDrop = AppSettings.IsUpdateBackDrop;
            AppViewModel.LyricsAlignment = ToolUtils.ConvertTextAlignmentToString(AppSettings.LyricsAlignment);
            AppViewModel.IsGlobalFontSizeEnabled = AppSettings.IsGlobalFontSizeEnabled;
            AppViewModel.GlobalFontSize = AppSettings.GlobalFontSize;
            AppViewModel.MusicCoverCache = AppSettings.MusicCoverCache;
            AppViewModel.DsdPcmFreq = AppSettings.dsdPcmFreq.ToString();
            AppViewModel.IsWFWLyrics = AppSettings.IsWFWLyrics;
            AppViewModel.LyricsBlurAmount = AppSettings.LyricsBlurAmount * 10;
        }

        public async Task SaveSettingAsync()
        {
            SaveSettings settings = await GetSettings();
            SaveSettings newSettings = SaveCurrentSettings(new SaveSettings(), settings?.equalizerStr);
            if (settings is null)
            {
                await InsertSettings(newSettings);
            }
            else
            {
                newSettings.Id = settings.Id;
                await UpdateSettings(newSettings);
            }
        }

        private SaveSettings SaveCurrentSettings(SaveSettings newSettings,string equalizerStr = null) {            
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.Latency = AppViewModel.Latency;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
            newSettings.DefualtEntry = AppViewModel.DefaultEntryComboBoxTag;
            newSettings.DefualtPlayList = AppViewModel.DefaultPlayListComboBoxTag;
            newSettings.AppStyle = AppViewModel.BackdropType;
            newSettings.AppTheme = AppViewModel.ThemeType;
            newSettings.isCoverCacheEnabled = AppViewModel.IsCoverCacheEnabled;
            newSettings.isRunningBackend = AppViewModel.IsRunningBackend;
            newSettings.isAutoLyricsEnabled = AppViewModel.IsAutoLyricsEnabled;
            newSettings.dsdGain = AppViewModel.DsdGain;
            newSettings.equalizerStr = equalizerStr ?? AppSettings.equalizerStr;
            newSettings.IsEqualizerEnabled = AppSettings.IsEqualizerEnabled;
            newSettings.EqualizerPreset = AppSettings.EqualizerPreset;
            newSettings.CoverSize = AppViewModel.CoverSize;
            newSettings.DrillInAnimationTime = AppViewModel.DrillInAnimationTime;
            newSettings.EntranceAnimationTime = AppViewModel.EntranceAnimationTime;
            newSettings.SlideAnimationTime = AppViewModel.SlideAnimationTime;
            newSettings.IsBackgroundCoverEnabled = AppViewModel.IsBackgroundCoverEnabled;
            newSettings.IsFolderWatchEnabled = AppViewModel.IsFolderWatchEnabled;
            newSettings.IsCustomAppSize = AppSettings.IsCustomAppSize;
            newSettings.AppHeight = AppSettings.AppHeight;
            newSettings.AppWidth = AppSettings.AppWidth;
            newSettings.GlobalFont = AppSettings.GlobalFont.Source;
            newSettings.CustomAcrylicOpacity = AppViewModel.CustomOpacity / 100;
            newSettings.CustomColorAlpha = AppViewModel.CustomColor.A;
            newSettings.CustomColorRed = AppViewModel.CustomColor.R;
            newSettings.CustomColorGreen = AppViewModel.CustomColor.G;
            newSettings.CustomColorBlue = AppViewModel.CustomColor.B;
            newSettings.IsUpdateBackDrop = AppSettings.IsUpdateBackDrop;
            newSettings.LyricsAlignment = ConvertTextAlignmentToString(AppSettings.LyricsAlignment);
            newSettings.LyricsMargin = (int)AppViewModel.LyricsMargin.Left;
            newSettings.GlobalFontSize = AppSettings.GlobalFontSize;
            newSettings.IsGlobalFontSizeEnabled = AppSettings.IsGlobalFontSizeEnabled;
            newSettings.MusicCoverCache = AppSettings.MusicCoverCache;
            newSettings.BassOutputDeviceId = AppSettings.BassOutputDeviceId;
            newSettings.IsDopEnabled = AppSettings.IsDopEnabled;
            newSettings.dsdPcmFreq = AppSettings.dsdPcmFreq;
            newSettings.IsPlayDetailBtnVisible = AppViewModel.IsPlayDetailButtonVisible;
            newSettings.IsFadeEnabled = AppSettings.IsFadeEnabled;
            newSettings.IsWFWLyrics = AppSettings.IsWFWLyrics;
            newSettings.LyricsBlurAmount = Math.Clamp(AppSettings.LyricsBlurAmount, 0, 1000);
            return newSettings;
        }

        public async Task SavePlayStateAsync(SavePlayState playState)
        {
            await _dbConnection.InsertOrReplaceAsync(playState);
        }

        public async Task SaveSettingsAsync(SaveSettings settings)
        {
            await _dbConnection.InsertOrReplaceAsync(settings);
        }

        public async Task RemoveMusic(int musicId)
        {
            try
            {
                await _dbConnection.DeleteAsync<Music>(musicId);
                await AppViewModel.AllSongs.ReplaceAllAsync(await _dbConnection.Table<Music>().ToListAsync());
                var usbMusicGroups = AppData.musicOnUsbDevice.AsValueEnumerable()
                    .GroupBy(u => u.Title)
                    .ToDictionary(g => g.Key, g => g.AsValueEnumerable().ToList());
                foreach (var music in AppViewModel.AllSongs)
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
        public async Task CancelMusicsFavourite(IEnumerable<Music> musics)
        {
            foreach (var music in musics)
            {
                music.IsFavorite = false;
            }
            await _dbConnection.UpdateAllAsync(musics);
        }
        public async Task AddToFavourite(Music music)
        {
            await _dbConnection.UpdateAsync(music);
        }

        public Music LoadCurrentPlayingMusic(int? lastPlayedMusicId)
        {
            return AppViewModel?.AllSongs?.FirstOrDefault(m => m.Id == lastPlayedMusicId);
        }

        public async Task SavePlayState(List<Music> currentPlayingList, PlayMode currentPlayMode, int? currentPlayingMusicId, double volume, string sortOrder)
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

        public async Task<List<Music>> GetMusicListByFolder(StorageFolder folder)
        {
            var musicFiles = new List<Music>();
            await addFolderService.GetMusicFilesRecursive(folder, musicFiles);
            return musicFiles;
        }

        public async Task ScanFolderAsync(StorageFolder folder, int folderId)
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

        public async Task RemoveFolder(int folderId)
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

        public async Task<Folder> GetFolder(int folderId)
        {
            return await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
        }

        public async Task CheckFolderBeforeAdd(StorageFolder folder)
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
                // 处理异常，例如权限不足等情况
                System.Diagnostics.Debug.WriteLine($"获取文件时出错: {ex.Message}");
            }

            return allFiles;
        }

        private async Task<Music> updateMusic(Music music)
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(music.Path);
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
                    System.Diagnostics.Debug.WriteLine($"重新扫描文件夹时出错: {ex.Message}");
                }
            }
        }

        public async Task RescanFolderByPath(string folderPath, bool isUpdate = true, bool isSingleFolder = false)
        {
            _toDelete.Clear();
            _toUpdate.Clear();
            _files.Clear();
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);

            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                _files.AddRange(currentFiles);
                _musicFilesInFolder = AppViewModel.AllSongs.AsValueEnumerable()
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
                //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                //{
                //    App.MainWindow.UpdateMusicList();
                //});
                App.Services.GetRequiredService<AppViewModel>().RefreshAllSongs();
            }
        }

        public async Task RescanFolderWithOutUpdateAll(string folderPath, bool isSingleFolder = false)
        {
            var toDelete = new ConcurrentBag<Music>();
            var files = new List<StorageFile>();
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            List<Music> musicFilesInFolder = null;
            if (isSingleFolder)
            {
                var currentFiles = await folder.GetFilesAsync();
                files.AddRange(currentFiles);
                musicFilesInFolder = AppViewModel.AllSongs.AsValueEnumerable()
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

        public async Task AddMusicList(IEnumerable<Music> _toAdd)
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

        public async Task UpdateMusicList(IEnumerable<Music> _toUpdate)
        {
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

        public async Task DeletedMusicList(IEnumerable<Music> toDelete)
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

        public async Task<List<UsbDeviceMusic>> GetUsbDeviceMusics(string uniqueDeviceId)
        {
            return await _dbConnection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == uniqueDeviceId).ToListAsync();
        }

        public async Task<List<UsbDeviceMusic>> RescanUsbDeviceFolderByPath(List<UsbDeviceMusic> usbDeviceMusics, string uniqueDeviceId, string folderPath, bool isSingleFolder = false)
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
                UsbDeviceMusic usbDeviceMusic = addFolderService.GetUsbDeviceMusicInfo(storageFile, folder.Path, uniqueDeviceId);
                usbDeviceMusicsInsertList.Add(usbDeviceMusic);
                await _dbConnection.InsertAsync(usbDeviceMusic);
            }
            return usbDeviceMusicsInsertList;
        }
    }
}
