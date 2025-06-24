using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
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

        public EventHandler<Music> playingAlbumMusic;
        public EventHandler<Music> playingArtistMusic;
        public EventHandler<Music> playingFolderMusic;
        //public static EventHandler rescanFolderStart;
        public EventHandler rescanFolderEnd;
        public EventHandler hideTransmission;
        public EventHandler showTransmission;

        /// <summary>
        /// 创建并显示右键菜单
        /// </summary>
        public async Task ShowAlbumContextMenu(object item, UIElement targetElement, Point position, string type)
        {
            // 创建菜单
            MenuFlyout flyout = new MenuFlyout();

            MenuFlyoutItem play = new MenuFlyoutItem
            {
                Text = ToolUtils.GetString("FlyoutPlay"),
                DataContext = item
            };
            play.Click += (sender, e) => Play_Click(sender, e, type);
            flyout.Items.Add(play);

            // 添加"添加到最爱"菜单项
            MenuFlyoutItem favoriteItem = new MenuFlyoutItem
            {
                Text = ToolUtils.GetString("FlyoutFavorite"),
                DataContext = item
            };
            favoriteItem.Click += (sender, e) => AddToFavourite_Click(sender, e, type);
            flyout.Items.Add(favoriteItem);

            // 创建"添加到播放列表"子菜单
            MenuFlyoutSubItem playlistSubItem = new MenuFlyoutSubItem
            {
                Text = ToolUtils.GetString("FlyoutAddToPlaylist"),
            };
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


            //List<UsbStorageDevice> usbStorageDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
            if (AppData.usbStorageDevices != null && AppData.usbStorageDevices.Count > 0)
            {
                MenuFlyoutSubItem usbDeviceSubItem = new MenuFlyoutSubItem
                {
                    Text = ToolUtils.GetString("SendToUsbDevice"),
                    Tag = "usbDevice",
                };

                foreach (var device in AppData.usbStorageDevices)
                {
                    MenuFlyoutItem usbDeviceItem = new MenuFlyoutItem
                    {
                        Text = $"{device.Name} , {ToolUtils.GetString("Path")}：{device.Path} , {ToolUtils.GetString("FreeSpace")}：{device.FreeSpaceInGB}GB",
                        DataContext = new { MusicItem = item, UsbStorageDevice = device }
                    };
                    usbDeviceItem.Click += (sender, e) => SendMusicToUsbDevice_Click(sender, e, type);
                    usbDeviceSubItem.Items.Add(usbDeviceItem);
                }
                flyout.Items.Add(usbDeviceSubItem);
            }
            if (type == "folder")
            {

                MenuFlyoutItem rescanItem = new MenuFlyoutItem
                {
                    Text = ToolUtils.GetString("Rescan"),
                    DataContext = item
                };
                rescanItem.Click += (sender, e) => RescanFolder_Click(sender, e, type);
                flyout.Items.Add(rescanItem);
            }

            if (type == "album")
            {
                MenuFlyoutItem properties = new MenuFlyoutItem
                {
                    Text = ToolUtils.GetString("FlyoutProperties"),
                    DataContext = item
                };
                properties.Click += (sender, e) => AlbumProperties_Click(sender, e, type);
                flyout.Items.Add(properties);
            }
            flyout.ShowAt(targetElement, position);

        }

        private async void SendMusicToUsbDevice_Click(object sender, RoutedEventArgs e, string type)
        {
            var menuItem = sender as MenuFlyoutItem;
            var data = menuItem.DataContext as dynamic;
            Music item = data.MusicItem as Music;
            UsbStorageDevice device = data.UsbStorageDevice;
            if (item != null)
            {
                List<Music> musicList = new List<Music>();
                if (type == "album")
                {
                    musicList = MusicDatabaseService.FindMusicListByAlbum(item.Album);
                }
                if (type == "artist")
                {
                    musicList = MusicDatabaseService.FindMusicListByArtist(item.Author);
                }
                if (type == "folder")
                {
                    musicList = MusicDatabaseService.FindMusicListByLastLevelFolderPath(item.LastLevelFolderPath);
                }
                if (musicList != null && musicList.Count > 0)
                {
                    showTransmission?.Invoke(this, EventArgs.Empty);
                    var usbWriter = new UsbWriterHelper();
                    usbWriter.hideTransmission += (s, e) =>
                    {
                        hideTransmission?.Invoke(this, EventArgs.Empty);
                    };
                    await usbWriter.WriteToUsb(musicList, device);
                    UsbDeviceSubFolderRescan usbDeviceSubFolderRescan = new UsbDeviceSubFolderRescan();
                    foreach (var music in musicList)
                    {
                        var existingMusic = AppData.musicOnUsbDevice.Where(m => m.Title == music.Title).FirstOrDefault();
                        if (existingMusic != null)
                        {
                            continue; // 如果已经存在，则跳过
                        }
                        UsbDeviceMusic usbDeviceMusic = new UsbDeviceMusic();
                        usbDeviceMusic.Title = music.Title;
                        usbDeviceMusic.Author = music.Author;
                        usbDeviceMusic.Album = music.Album;
                        usbDeviceMusic.Extension = music.Extension;
                        usbDeviceMusic.UniqueDeviceId = AppData.usbStorageDevice.UniqueId;
                        AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                    }
                }
            }
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
                    musicList = MusicDatabaseService.FindMusicListByAlbum(music.Album);
                }
                if (type == "artist")
                {
                    musicList = MusicDatabaseService.FindMusicListByArtist(music.Author);
                }
                if (type == "folder")
                {
                    musicList = MusicDatabaseService.FindMusicListByLastLevelFolderPath(music.LastLevelFolderPath);
                }
                if (musicList != null && musicList.Count > 0)
                {
                    await MusicDatabaseService.AddMusicListToFavour(musicList);
                    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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
                        await Task.Run(async() =>
                        {
                            await MusicDatabaseService.RescanFolderByPath(item.FolderPath);
                        });
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
                    musicList = MusicDatabaseService.FindMusicListByAlbum(item.Album);
                }
                if (type == "artist")
                {
                    musicList = MusicDatabaseService.FindMusicListByArtist(item.Author);
                }
                if (type == "folder")
                {
                    musicList = MusicDatabaseService.FindMusicListByLastLevelFolderPath(item.LastLevelFolderPath);
                }
                if (musicList != null && musicList.Count > 0)
                {
                    await MusicDatabaseService.AddMusicListToPlayList(musicList, playlist.Id);
                }
            }
        }
    }

}
