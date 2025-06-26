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
    public sealed partial class PlayListSongPage : Page, INavigatable
    {
        //private ObservableCollection<Music> musicList;
        //private MusicBrowsePage parentPage;
        //private string _lastSearchText = "";
        //private AudioConverterService converterService;
        //private ProgressDialog progressDialog;
        public PlayListSongViewModel ViewModel { get; }
        private readonly IMessenger _messenger;
        public PlayListSongPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<PlayListSongViewModel>();
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            _messenger = App.Services.GetRequiredService<IMessenger>();
            _messenger.Register<ScrollToMusicMessageHepler>(this, OnScrollToMusic);
            //MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            //converterService = new AudioConverterService();
            //progressDialog = new ProgressDialog(ToolUtils.GetString("Converting"));
            //progressDialog.Title = ToolUtils.GetString("Processing");
            //musicList = new ObservableCollection<Music>();
            //MusicListView.ItemsSource = musicList;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.ReceiveNavigation();
                //this.parentPage = parentPage;
                //parentPage.DisableBackButton();
                //parentPage.refreshPage += RefreshPlayList;
                //PlayListName.Text = parentPage.currentPlayList.Name;
                //initizeData();
                //clearUsbDeviceMusicList(null, null);
                //refreshUsbDeviceMusicList(null, null);
                //parentPage.clearUsbDeviceMusicList += clearUsbDeviceMusicList;
                //parentPage.refreshUsbDeviceMusicList += refreshUsbDeviceMusicList;
                //if (_lastSearchText != AppData.searchText || musicList == null || musicList.Count == 0 || PlayListName.Text!= parentPage.currentPlayList.Name)
                //{
                //    PlayListName.Text = parentPage.currentPlayList.Name;
                //    _lastSearchText = AppData.searchText;
                //    initizeData();
                //}
                //else
                //{
                //    UpdateMusicListView();
                //    Debug.WriteLine("搜索条件未变更，保留当前视图状态");
                //}                
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
        //                    .GroupBy(u => u.Title)
        //                    .ToDictionary(g => g.Key, g => g.ToList());
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

        //private void RefreshPlayList(object? sender, EventArgs e)
        //{
        //    initizeData();
        //}

        //private async void initizeData()
        //{
        //    if (parentPage != null)
        //    {
        //        var musicList = MusicDatabaseService.GetMusicByPlayListIdFromMem(parentPage.currentPlayListId, AppData.searchText);
        //        LoadMusicAsync(musicList);
        //    }
        //}

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.MusicListView_DragItemsCompleted();
            //if (parentPage != null)
            //{
            //    for (int i = 0; i < musicList.Count; i++)
            //    {
            //        musicList[i].PlayListOrder = musicList.Count - i;
            //        await MusicDatabaseService.UpdatePlayListMusicOrder(parentPage.currentPlayList.Id, musicList[i]);
            //    }
            //    await MusicDatabaseService.GetPlayListMusic();
            //}
        }

        //public void LoadMusicAsync(List<Music> musics)
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

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
            //var order = "DefaultOrder";
            //List<Music> musics = new List<Music>();
            //if (!string.IsNullOrEmpty(sortOrder))
            //{
            //    order = sortOrder;
            //}
            //if (musicList.Count > 0)
            //{
            //    musics = ToolUtils.SortMusicList("playList", order, musicList.ToList());
            //}
            //musicList.Clear();
            //foreach (var music in musics)
            //{
            //    musicList.Add(music);
            //}
        }

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
            //try
            //{
            //    if (parentPage.musicPlaybackService.currentPlayingMusic != null)
            //    {
            //        var selectedMusic = musicList.FirstOrDefault(music =>
            //        music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id);

            //        if (selectedMusic != null)
            //        {
            //            MusicListView.SelectedItem = selectedMusic;
            //            MusicListView.ScrollIntoView(selectedMusic);
            //        }
            //    }
            //    //if (musicList.Contains(parentPage.musicPlaybackService.currentPlayingMusic))
            //    //{
            //    //    Music selectedMusic = null;
            //    //    foreach (var music in musicList)
            //    //    {
            //    //        if (music.Id == parentPage.musicPlaybackService.currentPlayingMusic.Id)
            //    //        {
            //    //            selectedMusic = music;
            //    //        }
            //    //    }
            //    //    MusicListView.SelectedItem = selectedMusic;
            //    //    MusicListView.ScrollIntoView(selectedMusic);
            //    //}
            //}
            //catch (Exception ex)
            //{
            //    Debug.WriteLine($"滚动音乐失败: {ex.Message}");
            //}
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


        private async void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.PlayMenuItem_Click(uniqueSelectedMusics);
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

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.DeleteMenuItem_Click(uniqueSelectedMusics);
            //if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            //{
            //    foreach (Music item in uniqueSelectedMusics)
            //    {
            //        await MusicDatabaseService.RemoveMusicFromPlayList(parentPage.currentPlayListId, item.Id);
            //        musicList.Remove(item);
            //    }
            //}
            //else
            //{
            //    if (MusicListView.SelectedItem is Music selectedMusic)
            //    {
            //        await MusicDatabaseService.RemoveMusicFromPlayList(parentPage.currentPlayListId, selectedMusic.Id);
            //        musicList.Remove(selectedMusic);
            //    }
            //}
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.SetAsFavoriteMenuItem_Click(uniqueSelectedMusics);
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
            //if (MusicListView.SelectedItem is Music selectedMusic)
            //{
            //    await parentPage.AddToFavourite(selectedMusic);
            //}
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenInExplorer_Click();
            //if (MusicListView.SelectedItem is Music selectedMusic)
            //{
            //    var filePath = selectedMusic.Path;
            //    if (File.Exists(filePath))
            //    {
            //        try
            //        {
            //            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            //        }
            //        catch (Exception ex)
            //        {
            //            Debug.WriteLine($"打开资源管理器时出错: {ex.Message}");
            //        }
            //    }
            //    else
            //    {
            //        Debug.WriteLine($"文件不存在: {filePath}");
            //    }
            //}
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            MenuFlyoutItem? menuItem = sender as MenuFlyoutItem;
            await ViewModel.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
            //if (uniqueSelectedMusics != null && uniqueSelectedMusics.Count > 1)
            //{
            //    var menuItem = sender as MenuFlyoutItem;
            //    if (menuItem != null && menuItem.Tag.ToString() != null)
            //    {
            //        int progressBarValue = 0;
            //        progressDialog.RequestedTheme = AppSettings.elementTheme;
            //        await progressDialog.UpdateProgress(progressBarValue);
            //        converterService.updateProgress += (sender, progress) =>
            //        {
            //            if (progressBarValue < (int)progress)
            //            {
            //                progressBarValue = (int)progress;
            //            }
            //            if (progressBarValue < 100)
            //            {
            //                _ = progressDialog.UpdateProgress(progressBarValue);
            //            }
            //        };
            //        progressDialog.XamlRoot = this.XamlRoot;
            //        _ = progressDialog.ShowAsync();

            //        List<Task> conversionTasks = new List<Task>();
            //        foreach (Music item in uniqueSelectedMusics)
            //        {
            //            Task conversionTask = converterService.ConvertAudio2Wav(item, menuItem.Tag.ToString());
            //            conversionTasks.Add(conversionTask);
            //        }
            //        await Task.WhenAll(conversionTasks);
            //        _ = progressDialog.UpdateProgress(100);
            //    }
            //}
            //else
            //{
            //    var menuItem = sender as MenuFlyoutItem;
            //    if (menuItem != null && menuItem.Tag.ToString() != null)
            //    {
            //        if (MusicListView.SelectedItem is Music selectedMusic)
            //        {
            //            int progressBarValue = 0;
            //            progressDialog.RequestedTheme = AppSettings.elementTheme;
            //            _ = progressDialog.UpdateProgress(progressBarValue);
            //            _ = converterService.ConvertAudio2Wav(selectedMusic, menuItem.Tag.ToString());
            //            converterService.updateProgress += (sender, progress) =>
            //            {
            //                progressBarValue = (int)progress;
            //                _ = progressDialog.UpdateProgress(progressBarValue);
            //            };
            //            if (progressBarValue < 100)
            //            {
            //                progressDialog.XamlRoot = this.XamlRoot;
            //                _ = progressDialog.ShowAsync();
            //            }

            //        }
            //    }
            //}
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
                    //List<UsbStorageDevice> usbDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                    if (menuFlyout.Items.Count > 6)
                    {
                        MenuFlyoutSubItem fifthItem = menuFlyout.Items[3] as MenuFlyoutSubItem;
                        if (fifthItem != null)
                        {
                            if (fifthItem.Tag.ToString() == "usbDevice")
                            {
                                menuFlyout.Items.RemoveAt(3);
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
                        menuFlyout.Items.Insert(3, usbDeviceSubItem);
                    }
                }
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                // 假设 AlbumDetailsPage 是目标页面，将专辑名作为参数传递
                ViewModel.AlbumTextBlock_Tapped(albumName);
                //if (parentPage != null)
                //{
                //    parentPage.SelectBarAlbum(albumName);
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

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                await ViewModel.IsFavouriteIconButton_Click(music);
                //if (music != null)
                //{
                //    ((FontIcon)button.Content).Glyph = !music.IsFavorite ? "\ueb52" : "\ueb51";
                //    await parentPage.AddToFavourite(music);
                //    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                //}
            }
        }
    }
}
