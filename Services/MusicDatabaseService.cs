using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static WinUIMusicPlayer.Utils.ToolUtils;
using WinUIMusicPlayer.Model;
using System.IO;

namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService
    {
        private static SQLiteAsyncConnection _dbConnection;
        private static readonly string DbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");

        public static async Task Initialize()
        {
            if (_dbConnection == null)
            {
                _dbConnection = new SQLiteAsyncConnection(DbPath);
                await _dbConnection.CreateTableAsync<Music>();
                await _dbConnection.CreateTableAsync<Folder>();
                await _dbConnection.CreateTableAsync<SavePlayState>();
                await _dbConnection.CreateTableAsync<SaveSettings>();
            }
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

        public static async Task<List<Music>> GetMusicListAsync(string search = null)
        {
            var query = _dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            return await query.OrderBy(m => m.Title).ToListAsync();
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

        public static async Task<List<Music>> GetArtistMusicAsync(string artist, string search = null)
        {
            var query = _dbConnection.Table<Music>().Where(m => m.Author != null && m.Author.ToLower().Equals(artist.ToLower()));
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            return await query.OrderBy(m => m.Album).ToListAsync();
        }

        public static async Task<List<Music>> GetFolderMusicAsync(string folder, string search = null)
        {
            var query = _dbConnection.Table<Music>().Where(m => m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower()));
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return await query.OrderBy(m => m.LastLevelFolderPath).ToListAsync();
        }

        public static async Task<List<Music>> GetAlbumMusicAsync(string album, string search = null)
        {
            var query = _dbConnection.Table<Music>().Where(m => m.Album != null && m.Album.ToLower().Equals(album.ToLower()));
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            return await query.OrderBy(m => m.TrackNumber).ToListAsync();
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

        public static async Task<SaveSettings> GetSettingsAsync()
        {
            return await _dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
        }

        public static async Task SavePlayStateAsync(SavePlayState playState)
        {
            await _dbConnection.InsertOrReplaceAsync(playState);
        }

        public static async Task SaveSettingsAsync(SaveSettings settings)
        {
            await _dbConnection.InsertOrReplaceAsync(settings);
        }
    }
}
