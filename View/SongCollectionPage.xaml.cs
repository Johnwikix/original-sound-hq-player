using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SongCollectionPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        private AudioConverterService converterService;
        private ProgressDialog progressDialog;
        public SongCollectionPage()
        {
            this.InitializeComponent();
            converterService = new AudioConverterService();
            progressDialog = new ProgressDialog("正在转换");
            musicList = new ObservableCollection<Music>();
            MusicListView.ItemsSource = musicList;
        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.DisableBackButton();
                parentPage.refreshSong += RefreshSong;
                RefreshPage();
                clearUsbDeviceMusicList(null, null);
                refreshUsbDeviceMusicList(null, null);
                parentPage.refreshUsbDeviceMusicList += refreshUsbDeviceMusicList;
                parentPage.clearUsbDeviceMusicList += clearUsbDeviceMusicList;
            }
        }
        private void clearUsbDeviceMusicList(object? sender, EventArgs e)
        {
            foreach (var music in musicList)
            {
                music.IsExistOnDevice = 0;
            }
        }

        private void refreshUsbDeviceMusicList(object? sender, EventArgs e)
        {
            var usbMusicGroups = AppData.musicOnUsbDevice
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var music in musicList)
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
        private async void RefreshPage()
        {
            if (parentPage != null)
            {
                if (parentPage.pageType == "album")
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetAlbumMusicFromMem(parentPage.currentAlbumName, null));
                    await LoadMusicAsync(musics, parentPage.pageType);
                }
                if (parentPage.pageType == "artist")
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetArtistMusicFromMem(parentPage.currentArtistName, null));
                    await LoadMusicAsync(musics, parentPage.pageType);
                }
                if (parentPage.pageType == "folder")
                {
                    ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetFolderMusicFromMem(parentPage.currentFolderName, AppData.searchText));
                    await LoadMusicAsync(musics, parentPage.pageType);
                }
            }
        }

        private void RefreshSong(object? sender, EventArgs e)
        {
            RefreshPage();
        }

        public void SortMusicList(string sortOrder, string type)
        {
            var order = "DefaultOrder";
            List<Music> musics = new List<Music>();
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {

                musics = ToolUtils.SortMusicList(type, order, musicList.ToList());
            }
            musicList.Clear();
            foreach (var music in musics)
            {
                musicList.Add(music);
            }
            //MusicListView.ItemsSource = musicList;
        }

        public async Task LoadMusicAsync(ObservableCollection<Music> musics, string type = null)
        {
            try
            {
                musicList.Clear();
                foreach (var music in musics)
                {
                    musicList.Add(music);
                }
                if (type != null)
                {
                    SortMusicList("DefaultOrder", type);
                }
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateFavouriteMusic(Music music)
        {
            if (musicList != null && musicList.Count > 0)
            {
                var index = musicList.IndexOf(musicList.FirstOrDefault(m => m.Id == music.Id));
                if (index != -1)
                {
                    musicList[index].IsFavorite = music.IsFavorite;
                }
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage.musicPlaybackService.currentPlayingMusic != null)
                {
                    var selectedMusic = musicList.FirstOrDefault(music =>
                    music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {
                        MusicListView.SelectedItem = selectedMusic;
                        MusicListView.ScrollIntoView(selectedMusic);
                    }
                }
                //if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
                //{
                //    Music selectedMusic = null;
                //    foreach (var music in musicList)
                //    {
                //        if (music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id)
                //        {
                //            selectedMusic = music;
                //        }
                //    }
                //    MusicListView.SelectedItem = selectedMusic;
                //    MusicListView.ScrollIntoView(selectedMusic);
                //}
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            }
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = MusicListView.SelectedItem as Music;
            if (selectedMusic != null && parentPage != null)
            {
                parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
                await parentPage.PlayMusic(selectedMusic);
            }
        }

        private async void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                parentPage.musicPlaybackService.currentPlayingList = uniqueSelectedMusics;
                await parentPage.PlayMusic(uniqueSelectedMusics[0]);
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
                    await parentPage.PlayMusic(selectedMusic);
                }
            }
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await MusicDatabaseService.RemoveMusic(item.Id);
                    musicList.Remove(item);
                }
                if (parentPage != null)
                {
                    parentPage.MainWindow_updateMusicList(null, null);
                }
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    musicList.Remove(selectedMusic);
                    await MusicDatabaseService.RemoveMusic(selectedMusic.Id);
                    if (parentPage != null)
                    {
                        parentPage.MainWindow_updateMusicList(null, null);
                    }
                }
            }
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    await parentPage.AddToFavourite(item);
                }
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    await parentPage.AddToFavourite(selectedMusic);
                }
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                var filePath = selectedMusic.Path;
                if (File.Exists(filePath))
                {
                    try
                    {
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"打开资源管理器时出错: {ex.Message}");
                    }
                }
                else
                {
                    Debug.WriteLine($"文件不存在: {filePath}");
                }
            }
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                var menuItem = sender as MenuFlyoutItem;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    int progressBarValue = 0;
                    progressDialog.RequestedTheme = AppSettings.elementTheme;
                    await progressDialog.UpdateProgress(progressBarValue);
                    converterService.updateProgress += (sender, progress) =>
                    {
                        if (progressBarValue < (int)progress)
                        {
                            progressBarValue = (int)progress;
                        }
                        if (progressBarValue < 100)
                        {
                            _ = progressDialog.UpdateProgress(progressBarValue);
                        }
                    };
                    progressDialog.XamlRoot = this.XamlRoot;
                    _ = progressDialog.ShowAsync();

                    List<Task> conversionTasks = new List<Task>();
                    foreach (Music item in uniqueSelectedMusics)
                    {
                        Task conversionTask = converterService.ConvertAudio2Wav(item, menuItem.Tag.ToString());
                        conversionTasks.Add(conversionTask);
                    }
                    await Task.WhenAll(conversionTasks);
                    _ = progressDialog.UpdateProgress(100);
                }
            }
            else
            {
                var menuItem = sender as MenuFlyoutItem;
                if (menuItem != null && menuItem.Tag.ToString() != null)
                {
                    if (MusicListView.SelectedItem is Music selectedMusic)
                    {
                        int progressBarValue = 0;
                        progressDialog.RequestedTheme = AppSettings.elementTheme;
                        _ = progressDialog.UpdateProgress(progressBarValue);
                        _ = converterService.ConvertAudio2Wav(selectedMusic, menuItem.Tag.ToString());
                        converterService.updateProgress += (sender, progress) =>
                        {
                            progressBarValue = (int)progress;
                            _ = progressDialog.UpdateProgress(progressBarValue);
                        };
                        if (progressBarValue < 100)
                        {
                            progressDialog.XamlRoot = this.XamlRoot;
                            _ = progressDialog.ShowAsync();
                        }

                    }
                }
            }
        }

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                if (music != null)
                {
                    ((FontIcon)button.Content).Glyph = !music.IsFavorite ? "\ueb52" : "\ueb51";
                    await parentPage.AddToFavourite(music);
                    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                }
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                if (parentPage != null)
                {
                    parentPage.SelectBarArtist(artist);
                }
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                if (parentPage != null)
                {
                    parentPage.SelectBarAlbum(albumName);
                }
            }
        }

        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            if (MusicListView.SelectedItem is Music selectedMusic)
            {
                var musicDetailsWindow = new MusicDetailsWindow(selectedMusic);
                musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
                musicDetailsWindow.Activate();
            }
        }

        private async void MusicDetailsWindow_MusicDetailChanged(object? sender, Music musicItem)
        {
            foreach (var music in musicList)
            {
                if (music.Path == musicItem.Path)
                {
                    music.Title = musicItem.Title;
                    music.Author = musicItem.Author;
                    music.Album = musicItem.Album;
                    music.Year = musicItem.Year;
                    music.TrackNumber = musicItem.TrackNumber;
                    music.Lyrics = musicItem.Lyrics;
                    break;
                }
            }
        }

        private List<Music> GetUniqueSelectedItems()
        {
            List<Music> uniqueItems = new List<Music>();
            var selectedItems = MusicListView.SelectedItems;
            foreach (var item in selectedItems)
            {
                if (item is Music music)
                {
                    uniqueItems.Add(music);
                }
            }
            return uniqueItems;
        }

        private async void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var targetElement = e.OriginalSource as FrameworkElement;
            ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            if (listViewItem != null)
            {
                var musicItem = listViewItem.Content as Model.Music;
                // 检查当前指向的元素是否已在选中项列表中
                bool isCurrentItemSelected = false;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is Music selectedMusic && musicItem != null && selectedMusic.Id == musicItem.Id)
                    {
                        isCurrentItemSelected = true;
                        break;
                    }
                }
                // 如果当前项不在选中列表中，则清除现有选择并选中当前项
                if (!isCurrentItemSelected)
                {
                    MusicListView.SelectedItems.Clear();
                    listViewItem.IsSelected = true;
                    MusicListView.SelectedItem = musicItem;
                }
                List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
                // 设置右键菜单
                if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem != null)
                {
                    // 为菜单项设置DataContext
                    foreach (var menuItem in flyout.Items)
                    {
                        menuItem.DataContext = musicItem;
                    }
                    var addToPlaylistSubItem = flyout.Items[2] as MenuFlyoutSubItem;
                    addToPlaylistSubItem.Items.Clear();
                    var playlists = await MusicDatabaseService.GetPlayListAsync();
                    foreach (var playlist in playlists)
                    {
                        var menuItem = new MenuFlyoutItem
                        {
                            Text = playlist.Name
                        };
                        menuItem.Click += async (s, args) =>
                        {
                            // 多选情况：添加所有选中的歌曲到播放列表
                            if (uniqueSelectedMusics.Count > 1)
                            {
                                foreach (var music in uniqueSelectedMusics)
                                {
                                    await MusicDatabaseService.AddMusicToPlayList(playlist.Id, music.Id);
                                }
                                // 可以添加一个提示通知，表明多个歌曲已添加到播放列表
                                Debug.WriteLine($"已添加 {uniqueSelectedMusics.Count} 首歌曲到播放列表: {playlist.Name}");
                            }
                            // 单选情况：只添加当前右键点击的歌曲
                            else if (musicItem != null)
                            {
                                await MusicDatabaseService.AddMusicToPlayList(playlist.Id, musicItem.Id);
                                Debug.WriteLine($"已添加歌曲 '{musicItem.Title}' 到播放列表: {playlist.Name}");
                            }
                        };
                        addToPlaylistSubItem.Items.Add(menuItem);
                    }
                    //List<UsbStorageDevice> usbDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                    if (menuFlyout.Items.Count > 7)
                    {
                        MenuFlyoutSubItem fifthItem = menuFlyout.Items[4] as MenuFlyoutSubItem;
                        if (fifthItem != null)
                        {
                            if (fifthItem.Tag.ToString() == "usbDevice")
                            {
                                menuFlyout.Items.RemoveAt(4);
                            }
                        }
                    }
                    if (AppData.usbStorageDevices != null && AppData.usbStorageDevices.Count > 0)
                    {
                        MenuFlyoutSubItem usbDeviceSubItem = new MenuFlyoutSubItem
                        {
                            Text = ToolUtils.GetString("SendToUsbDevice"),
                            Tag = "usbDevice",
                        };
                        foreach (var usbDevice in AppData.usbStorageDevices)
                        {
                            var menuItem = new MenuFlyoutItem
                            {
                                Text = $"{usbDevice.Name} , {ToolUtils.GetString("Path")}：{usbDevice.Path} , {ToolUtils.GetString("FreeSpace")}：{usbDevice.FreeSpaceInGB}GB",
                                Tag = usbDevice.Path
                            };
                            menuItem.Click += async (s, args) =>
                            {
                                if (uniqueSelectedMusics.Count > 1)
                                {
                                    parentPage.ShowTransmission();
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        parentPage.HideTransmission();
                                    };
                                    await usbWriter.WriteToUsb(uniqueSelectedMusics, usbDevice);
                                    foreach (var music in uniqueSelectedMusics)
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
                                else if (musicItem != null)
                                {
                                    parentPage.ShowTransmission();
                                    List<Music> musicItems = new List<Music> { musicItem };
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        parentPage.HideTransmission();
                                    };
                                    await usbWriter.WriteToUsb(musicItems, usbDevice);
                                    UsbDeviceMusic usbDeviceMusic = new UsbDeviceMusic();
                                    usbDeviceMusic.Title = musicItem.Title;
                                    usbDeviceMusic.Author = musicItem.Author;
                                    usbDeviceMusic.Album = musicItem.Album;
                                    usbDeviceMusic.Extension = musicItem.Extension;
                                    usbDeviceMusic.UniqueDeviceId = AppData.usbStorageDevice.UniqueId;
                                    AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                                }
                                refreshUsbDeviceMusicList(null, null);
                            };
                            usbDeviceSubItem.Items.Add(menuItem);
                        }
                        menuFlyout.Items.Insert(4, usbDeviceSubItem);
                    }
                }
            }
        }
    }
}
