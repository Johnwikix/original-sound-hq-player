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
using WinUIMusicPlayer.Reader;
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
    public sealed partial class SongListPage : Page
    {
        private ObservableCollection<Music> musicList;
        private MusicBrowsePage parentPage;
        private string _lastSearchText = "";
        private AudioConverterService converterService;
        private ProgressDialog progressDialog;
        public SongListPage()
        {
            InitializeComponent();
            converterService = new AudioConverterService();
            progressDialog = new ProgressDialog("正在转换");
            musicList = new ObservableCollection<Music>();
            MusicListView.ItemsSource = musicList;
        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.refreshPage += RefreshPage;
                if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                {
                    _lastSearchText = AppData.searchText;
                    InitializeDatabase();
                }
                else
                {
                    UpdateMusicListView();
                    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                }
            }
        }

        private void RefreshPage(object? sender, EventArgs e)
        {
            InitializeDatabase();
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            ObservableCollection<Music> musics = new ObservableCollection<Music>();
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musics = new ObservableCollection<Music>(ToolUtils.SortMusicList("song", order, musicList.ToList()));
            }
            musicList.Clear();
            foreach (var music in musics)
            {
                musicList.Add(music);
            }
        }

        private async void InitializeDatabase()
        {
            ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetMusicListFromMem(AppData.searchText));
            LoadMusicAsync(musics);
        }

        public void LoadMusicAsync(ObservableCollection<Music> musics)
        {
            try
            {
                musicList.Clear();
                foreach (var music in musics)
                {
                    musicList.Add(music);
                }
                UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public void UpdateMusicListView()
        {
            try
            {
                if (parentPage != null)
                {
                    if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
                    {
                        Music selectedMusic = null;
                        foreach (var music in musicList)
                        {
                            if (music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id)
                            {
                                selectedMusic = music;
                            }
                        }
                        MusicListView.SelectedItem = selectedMusic;
                        MusicListView.ScrollIntoView(selectedMusic);
                    }
                }
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

        public void UpdateFavouriteMusic(Music music)
        {
            if (musicList != null && musicList.Count > 0)
            {
                var index = musicList.IndexOf(musicList.FirstOrDefault(m => m.Id == music.Id));
                if (index != -1)
                {
                    musicList[index].isFavorite = music.isFavorite;
                }
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

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    musicList.Remove(item);
                    await MusicDatabaseService.RemoveMusic(item.Id);
                }
            }
            else
            {
                if (MusicListView.SelectedItem is Music selectedMusic)
                {
                    musicList.Remove(selectedMusic);
                    await MusicDatabaseService.RemoveMusic(selectedMusic.Id);
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
                    List<UsbStorageDevice> usbDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                    var usbDeviceSubItem = flyout.Items[4] as MenuFlyoutSubItem;                    
                    usbDeviceSubItem.Items.Clear();
                    foreach (var usbDevice in usbDevices)
                    {
                        var menuItem = new MenuFlyoutItem
                        {
                            Text = $"路径：{usbDevice.Path}，剩余容量：{usbDevice.FreeSpaceInGB}GB",
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
                                _ = usbWriter.WriteToUsb(uniqueSelectedMusics, usbDevice);                              
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
                                _ = usbWriter.WriteToUsb(musicItems, usbDevice);
                            }
                        };
                        usbDeviceSubItem.Items.Add(menuItem);
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
                    ((FontIcon)button.Content).Glyph = !music.isFavorite ? "\ueb52" : "\ueb51";
                    await parentPage.AddToFavourite(music);
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
    }
}
