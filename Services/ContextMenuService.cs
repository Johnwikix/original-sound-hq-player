using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Input;
using WinUIMusicPlayer.Model;
using System.Diagnostics;
using Windows.Foundation;

namespace WinUIMusicPlayer.Services
{
    public class ContextMenuService
    {
        private static ContextMenuService _instance;
        public static ContextMenuService Instance => _instance ??= new ContextMenuService();
        public ContextMenuService() { }

        /// <summary>
        /// 创建并显示右键菜单
        /// </summary>
        public async Task ShowAlbumContextMenu(object item, UIElement targetElement, Point position, string type)
        {
            // 创建菜单
            MenuFlyout flyout = new MenuFlyout();

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
            if (type == "folder") {

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

            // 显示菜单
            flyout.ShowAt(targetElement, position);
        }
        private async void AddToFavourite_Click(object sender, RoutedEventArgs e,string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var music = menuItem?.DataContext as Music;
            Debug.WriteLine($"专辑名称: {music.Album}, 艺术家: {music.Author}");
            if (music != null)
            {
                List<Music> musicList = new List<Music>();
                if (type == "album") {
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
            
        }

        private async void AddToPlaylistMenuItem_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var data = menuItem.DataContext as dynamic;

            Music item = data.Item as Music;
            PlayList playlist = data.Playlist;

            if (item != null) {
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
