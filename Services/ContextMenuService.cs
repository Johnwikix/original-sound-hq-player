using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;

namespace WinUIMusicPlayer.Services
{
    public class ContextMenuService
    {
        private static ContextMenuService _instance;
        public static ContextMenuService Instance => _instance ??= new ContextMenuService();
        public ContextMenuService() { }

        private AlbumPage albumPage;

        public void SetAlbumPage(AlbumPage page)
        {
            albumPage = page;
        }

        public static EventHandler<Music> playingAlbumMusic;
        public static EventHandler<Music> playingArtistMusic;
        public static EventHandler<Music> playingFolderMusic;
        //public static EventHandler rescanFolderStart;
        public static EventHandler rescanFolderEnd;

        /// <summary>
        /// 创建并显示右键菜单
        /// </summary>
        public async Task ShowAlbumContextMenu(object item, UIElement targetElement, Point position, string type)
        {
            // 创建菜单
            MenuFlyout flyout = new MenuFlyout();

            MenuFlyoutItem play = new MenuFlyoutItem
            {
                Text = "播放",
                DataContext = item
            };
            play.Click += (sender, e) => Play_Click(sender, e, type);
            flyout.Items.Add(play);

            // 添加"添加到最爱"菜单项
            MenuFlyoutItem favoriteItem = new MenuFlyoutItem
            {
                Text = "添加到最爱",
                DataContext = item
            };
            favoriteItem.Click += (sender, e) => AddToFavourite_Click(sender, e, type);
            flyout.Items.Add(favoriteItem);

            // 创建"添加到播放列表"子菜单
            MenuFlyoutSubItem playlistSubItem = new MenuFlyoutSubItem
            {
                Text = "添加到播放列表"
            };
            if (type == "folder")
            {

                MenuFlyoutItem rescanItem = new MenuFlyoutItem
                {
                    Text = "重新扫描",
                    DataContext = item
                };
                rescanItem.Click += (sender, e) => RescanFolder_Click(sender, e, type);
                flyout.Items.Add(rescanItem);
            }

            // 获取所有播放列表
            List<PlayList> playlists = await MusicDatabaseService.GetPlayListAsync();
            foreach (PlayList playlist in playlists)
            {
                MenuFlyoutItem menuItem = new MenuFlyoutItem
                {
                    Text = playlist.Name,
                    DataContext = new { Item = item, Playlist = playlist }
                };
                menuItem.Click += (sender, e) => AddToPlaylistMenuItem_Click(sender, e, type);
                playlistSubItem.Items.Add(menuItem);
            }

            flyout.Items.Add(playlistSubItem);

            if (type == "album")
            {
                MenuFlyoutItem properties = new MenuFlyoutItem
                {
                    Text = "属性",
                    DataContext = item
                };
                properties.Click += (sender, e) => AlbumProperties_Click(sender, e, type);
                flyout.Items.Add(properties);
            }
            flyout.ShowAt(targetElement, position);

        }

        private void Play_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var music = menuItem?.DataContext as Music;
            if (music != null)
            {
                if (type == "album")
                {
                    playingAlbumMusic?.Invoke(this, music);
                }
                if (type == "artist")
                {
                    playingArtistMusic?.Invoke(this, music);
                }
                if (type == "folder")
                {
                    playingFolderMusic?.Invoke(this, music);
                }

            }
        }

        private async void AddToFavourite_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var music = menuItem?.DataContext as Music;
            if (music != null)
            {
                List<Music> musicList = new List<Music>();
                if (type == "album")
                {
                    musicList = await MusicDatabaseService.FindMusicListByAlbum(music.Album);
                }
                if (type == "artist")
                {
                    musicList = await MusicDatabaseService.FindMusicListByArtist(music.Author);
                }
                if (type == "folder")
                {
                    musicList = await MusicDatabaseService.FindMusicListByLastLevelFolderPath(music.LastLevelFolderPath);
                }
                if (musicList != null && musicList.Count > 0)
                {
                    _ = MusicDatabaseService.AddMusicListToFavour(musicList);
                }
            }
        }

        private async void RescanFolder_Click(object sender, RoutedEventArgs e, string type)
        {
            try
            {
                //rescanFolderStart?.Invoke(this, EventArgs.Empty);
                var menuItem = sender as MenuFlyoutItem;
                var item = menuItem?.DataContext as Music;
                if (item != null)
                {
                    if (!string.IsNullOrEmpty(item.FolderPath))
                    {
                        await MusicDatabaseService.RescanFolderByPath(item.FolderPath);
                    }
                }
            }
            finally
            {
                rescanFolderEnd?.Invoke(this, EventArgs.Empty);
            }

        }

        private async void AlbumProperties_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var item = menuItem?.DataContext as Music;

            if (item != null)
            {
                var albumDetailWindow = new AlbumDetailWindow(item);
                if (albumPage != null)
                {
                    albumDetailWindow.AlbumDetailChanged += albumPage.OnAlbumDetailChanged;
                }
                albumDetailWindow.Activate();
            }

        }

        private async void AddToPlaylistMenuItem_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var data = menuItem.DataContext as dynamic;

            Music item = data.Item as Music;
            PlayList playlist = data.Playlist;

            if (item != null)
            {
                List<Music> musicList = new List<Music>();
                if (type == "album")
                {
                    musicList = await MusicDatabaseService.FindMusicListByAlbum(item.Album);
                }
                if (type == "artist")
                {
                    musicList = await MusicDatabaseService.FindMusicListByArtist(item.Author);
                }
                if (type == "folder")
                {
                    musicList = await MusicDatabaseService.FindMusicListByLastLevelFolderPath(item.LastLevelFolderPath);
                }
                if (musicList != null && musicList.Count > 0)
                {
                    _ = MusicDatabaseService.AddMusicListToPlayList(musicList, playlist.Id);
                }
            }
        }
    }

}
