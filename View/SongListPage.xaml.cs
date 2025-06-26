using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
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
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SongListPage : Page, INavigatable
    {
        //private ObservableCollection<Music> musicList;
        //private MusicBrowsePage parentPage;
        //private string _lastSearchText = "";
        //private AudioConverterService converterService;
        //private ProgressDialog progressDialog;
        public SongListViewModel ViewModel { get;}
        private readonly IMessenger _messenger;
        public SongListPage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SongListViewModel>();
            ViewModel.SetCurrentPage(this);
            _messenger = App.Services.GetRequiredService<IMessenger>();
            _messenger.Register<ScrollToMusicMessageHepler>(this, OnScrollToMusic);
            DataContext = this;
            //converterService = new AudioConverterService();
            //progressDialog = new ProgressDialog("正在转换");
            //musicList = new ObservableCollection<Music>();
            //MusicListView.ItemsSource = musicList;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.ReceiveNavigation();
                //this.parentPage = parentPage;
                //parentPage.refreshPage += RefreshPage;
                //parentPage.refreshUsbDeviceMusicList += refreshUsbDeviceMusicList;
                //parentPage.clearUsbDeviceMusicList += clearUsbDeviceMusicList;
                //if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0)
                //{
                //    _lastSearchText = AppData.searchText;
                //    InitializeDatabase();
                //}
                //else
                //{
                //    UpdateMusicListView();
                //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                //}
                //clearUsbDeviceMusicList(null, null);
                //refreshUsbDeviceMusicList(null, null);
            }
        }
        private void OnScrollToMusic(object recipient, ScrollToMusicMessageHepler message)
        {
            // 在UI线程上执行
            DispatcherQueue.TryEnqueue(() =>
            {
                MusicListView.ScrollIntoView(message.SelectedMusic);
            });
        }
        //private void clearUsbDeviceMusicList(object? sender, EventArgs e)
        //{
        //    foreach (var music in musicList)
        //    {
        //        music.IsExistOnDevice = 0;
        //    }
        //}

        //private void refreshUsbDeviceMusicList(object? sender, EventArgs e)
        //{
        //    var usbMusicGroups = AppData.musicOnUsbDevice
        //                                .GroupBy(u => u.Title)
        //                                .ToDictionary(g => g.Key, g => g.ToList());
        //    foreach (var music in musicList)
        //    {
        //        music.IsExistOnDevice = 0;

        //        if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
        //        {
        //            music.IsExistOnDevice = 1;
        //            foreach (var usbMusic in matchingItems)
        //            {
        //                if (music.Author == usbMusic.Author &&
        //                    music.Album == usbMusic.Album &&
        //                    music.Extension == usbMusic.Extension)
        //                {
        //                    music.IsExistOnDevice = 2;
        //                    break;
        //                }
        //            }
        //        }
        //    }
        //}

        //private void RefreshPage(object? sender, EventArgs e)
        //{
        //    InitializeDatabase();
        //}

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
            //var order = "DefaultOrder";
            //ObservableCollection<Music> musics = new ObservableCollection<Music>();
            //if (!string.IsNullOrEmpty(sortOrder))
            //{
            //    order = sortOrder;
            //}
            //if (musicList.Count > 0)
            //{
            //    musics = new ObservableCollection<Music>(ToolUtils.SortMusicList("song", order, musicList.ToList()));
            //}
            //musicList.Clear();
            //foreach (var music in musics)
            //{
            //    musicList.Add(music);
            //}
        }

        //private async void InitializeDatabase()
        //{
        //    ObservableCollection<Music> musics = new ObservableCollection<Music>(MusicDatabaseService.GetMusicListFromMem(AppData.searchText));
        //    LoadMusicAsync(musics);
        //}

        //public void LoadMusicAsync(ObservableCollection<Music> musics)
        //{
        //    try
        //    {
        //        musicList.Clear();
        //        foreach (var music in musics)
        //        {
        //            musicList.Add(music);
        //        }
        //        SortMusicList(AppData.sortOrder);
        //        UpdateMusicListView();
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
        //    }
        //}

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
            //try
            //{
            //    if (parentPage != null)
            //    {
            //        if (parentPage.musicPlaybackService.currentPlayingMusic !=null)
            //        {
            //            var selectedMusic = musicList.FirstOrDefault(music =>
            //            music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id);

            //            if (selectedMusic != null)
            //            {
            //                MusicListView.SelectedItem = selectedMusic;
            //                MusicListView.ScrollIntoView(selectedMusic);
            //            }
            //        }                    
            //    }
            //}
            //catch (Exception ex)
            //{
            //    Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            //}
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
            //var selectedMusic = MusicListView.SelectedItem as Music;
            //if (selectedMusic != null && parentPage != null)
            //{
            //    parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
            //    await parentPage.PlayMusic(selectedMusic);
            //}
        }

        public void UpdateFavouriteMusic(Music music)
        {
            ViewModel.UpdateFavouriteMusic(music);
            //if (musicList != null && musicList.Count > 0)
            //{
            //    var index = musicList.IndexOf(musicList.FirstOrDefault(m => m.Id == music.Id));
            //    if (index != -1)
            //    {
            //        musicList[index].IsFavorite = music.IsFavorite;
            //    }
            //}
        }

        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.PlayMenuItem_Click(uniqueSelectedMusics);
            //if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            //{
            //    parentPage.musicPlaybackService.currentPlayingList = uniqueSelectedMusics;
            //    await parentPage.PlayMusic(uniqueSelectedMusics[0]);
            //}
            //else
            //{
            //    if (MusicListView.SelectedItem is Music selectedMusic)
            //    {
            //        parentPage.musicPlaybackService.currentPlayingList = musicList.ToList();
            //        await parentPage.PlayMusic(selectedMusic);
            //    }
            //}
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            MenuFlyoutItem menuItem = sender as MenuFlyoutItem;
            ViewModel.ConvertAudio_Click(uniqueSelectedMusics, menuItem);            
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel?.DeleteMenuItem_Click(uniqueSelectedMusics);
            //if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            //{
            //    foreach (Music item in uniqueSelectedMusics)
            //    {
            //        musicList.Remove(item);
            //        await MusicDatabaseService.RemoveMusic(item.Id);
            //    }
            //}
            //else
            //{
            //    if (MusicListView.SelectedItem is Music selectedMusic)
            //    {
            //        musicList.Remove(selectedMusic);
            //        await MusicDatabaseService.RemoveMusic(selectedMusic.Id);
            //    }
            //}
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.SetAsFavoriteMenuItem_Click(uniqueSelectedMusics);
            //if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            //{
            //    foreach (Music item in uniqueSelectedMusics)
            //    {
            //        await parentPage.AddToFavourite(item);
            //    }
            //}
            //else
            //{
            //    if (MusicListView.SelectedItem is Music selectedMusic)
            //    {
            //        await parentPage.AddToFavourite(selectedMusic);
            //    }
            //}
            //AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
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
                                DateTime startTime = DateTime.Now;
                                if (uniqueSelectedMusics.Count > 1)
                                {
                                    Debug.WriteLine($"消耗时间： {(DateTime.Now - startTime).TotalMilliseconds} 毫秒");
                                    ViewModel.ShowTransmission();
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        ViewModel.HideTransmission();
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
                                    Debug.WriteLine($"消耗时间： {(DateTime.Now - startTime).TotalMilliseconds} 毫秒");
                                    ViewModel.ShowTransmission();
                                    List<Music> musicItems = new List<Music> { musicItem };
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        ViewModel.HideTransmission();
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
                                ViewModel.RefreshUsbDeviceMusicList(null, null);
                            };
                            usbDeviceSubItem.Items.Add(menuItem);
                        }
                        menuFlyout.Items.Insert(4, usbDeviceSubItem);
                    }
                }
            }
        }

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                ViewModel.IsFavouriteIconButton_Click(music);
                //if (music != null)
                //{
                //    //((FontIcon)button.Content).Glyph = !music.IsFavorite ? "\ueb52" : "\ueb51";
                //    await parentPage.AddToFavourite(music);
                //    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                //}
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                ViewModel.AuthorTextBlock_Tapped(artist);
                //if (parentPage != null)
                //{
                //    parentPage.SelectBarArtist(artist);
                //}
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                ViewModel.AlbumTextBlock_Tapped(albumName);
                //if (parentPage != null)
                //{
                //    parentPage.SelectBarAlbum(albumName);
                //}
            }
        }

        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.MusicDetail_Click();
            //if (MusicListView.SelectedItem is Music selectedMusic)
            //{
            //    var musicDetailsWindow = new MusicDetailsWindow(selectedMusic);
            //    musicDetailsWindow.MusicDetailChanged += MusicDetailsWindow_MusicDetailChanged;
            //    musicDetailsWindow.Activate();
            //}
        }

        //private async void MusicDetailsWindow_MusicDetailChanged(object? sender, Music musicItem)
        //{
        //    foreach (var music in musicList)
        //    {
        //        if (music.Path == musicItem.Path)
        //        {
        //            music.Title = musicItem.Title;
        //            music.Author = musicItem.Author;
        //            music.Album = musicItem.Album;
        //            music.Year = musicItem.Year;
        //            music.TrackNumber = musicItem.TrackNumber;
        //            music.Lyrics = musicItem.Lyrics;
        //            break;
        //        }
        //    }
        //}
    }
}
