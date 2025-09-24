using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FavouritePlayListPage : Page, INavigatable
    {
        public FavouritePlayListViewModel ViewModel { get; }

        public FavouritePlayListPage(FavouritePlayListViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
        }

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await ViewModel.DragItems();
        }

        public void OnScrollToMusic(Music selectedMusic)
        {
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MusicListView.ScrollIntoView(selectedMusic);
                });
            });
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.ReceiveNavigation();
        }

        private void RefreshMusicList(object? sender, EventArgs e)
        {
            ViewModel.InitializeData();
        }

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
        }

        public void UpdateFavouriteMusic(Music music)
        {
            if (music.IsFavorite)
            {
                AddMusicToTop(music);
            }
            else
            {
                RemoveMusic(music);
            }
        }

        private void AddMusicToTop(Music newMusic)
        {
            ViewModel.AddMusicToTop(newMusic);
        }

        private void RemoveMusic(Music musicToRemove)
        {
            ViewModel.RemoveMusic(musicToRemove);
        }

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            Music selectedMusic = MusicListView.SelectedItem as Music;
            ViewModel.MusicListView_DoubleTapped(selectedMusic);
        }


        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.PlayMenuItem_Click(uniqueSelectedMusics);
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (await ViewModel.IsDeleteFromDisk())
            {
                IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
                ViewModel.DeleteMenuItem_Click(uniqueSelectedMusics);
            }
        }

        private void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.SetAsFavoriteMenuItem_Click(uniqueSelectedMusics);
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenInExplorer_Click();
        }

        private void ReGetLyrics_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.ReGetLyrics_Click(uniqueSelectedMusics);
        }
        private IEnumerable<Music> GetUniqueSelectedItems()
        {
            var selectedItems = MusicListView.SelectedItems;
            foreach (var item in selectedItems)
            {
                if (item is Music music)
                {
                    yield return music;
                }
            }
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            MenuFlyoutItem? menuItem = sender as MenuFlyoutItem;
            ViewModel.ConvertAudio_Click(menuItem, uniqueSelectedMusics);
        }

        private async void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var targetElement = e.OriginalSource as FrameworkElement;
            ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            if (listViewItem is not null)
            {
                var musicItem = listViewItem.Content as Model.Music;
                // 检查当前指向的元素是否已在选中项列表中
                bool isCurrentItemSelected = false;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is Music selectedMusic && musicItem is not null && selectedMusic.Id == musicItem.Id)
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
                IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
                // 设置右键菜单
                if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem is not null)
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
                            if (uniqueSelectedMusics.Count() > 1)
                            {
                                foreach (var music in uniqueSelectedMusics)
                                {
                                    await MusicDatabaseService.AddMusicToPlayList(playlist.Id, music.Id);
                                }
                                // 可以添加一个提示通知，表明多个歌曲已添加到播放列表
                                Debug.WriteLine($"已添加 {uniqueSelectedMusics.Count()} 首歌曲到播放列表: {playlist.Name}");
                            }
                            // 单选情况：只添加当前右键点击的歌曲
                            else if (musicItem is not null)
                            {
                                await MusicDatabaseService.AddMusicToPlayList(playlist.Id, musicItem.Id);
                                Debug.WriteLine($"已添加歌曲 '{musicItem.Title}' 到播放列表: {playlist.Name}");
                            }
                        };
                        addToPlaylistSubItem.Items.Add(menuItem);
                    }
                    //List<UsbStorageDevice> usbDevices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                    if (menuFlyout.Items.Count > 9)
                    {
                        MenuFlyoutSubItem fifthItem = menuFlyout.Items[4] as MenuFlyoutSubItem;
                        if (fifthItem is not null)
                        {
                            if (fifthItem.Tag.ToString() == "usbDevice")
                            {
                                menuFlyout.Items.RemoveAt(4);
                            }
                        }
                    }
                    if (AppData.usbStorageDevices is not null && AppData.usbStorageDevices.Count > 0)
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
                                if (uniqueSelectedMusics.Count() > 1)
                                {
                                    ViewModel.ShowTransmission();
                                    using (var usbWriter = new UsbWriterHelper())
                                    {
                                        usbWriter.hideTransmission += (sender, args) =>
                                        {
                                            ViewModel.HideTransmission();
                                        };
                                        await usbWriter.WriteToUsb(uniqueSelectedMusics, usbDevice);
                                    }
                                    foreach (var music in uniqueSelectedMusics)
                                    {
                                        var existingMusic = AppData.musicOnUsbDevice.Where(m => m.Title == music.Title).FirstOrDefault();
                                        if (existingMusic is not null)
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
                                else if (musicItem is not null)
                                {
                                    ViewModel.ShowTransmission();
                                    List<Music> musicItems = new List<Music> { musicItem };
                                    using (var usbWriter = new UsbWriterHelper())
                                    {
                                        usbWriter.hideTransmission += (sender, args) =>
                                        {
                                            ViewModel.HideTransmission();
                                        };
                                        await usbWriter.WriteToUsb(musicItems, usbDevice);
                                    }
                                    UsbDeviceMusic usbDeviceMusic = new UsbDeviceMusic();
                                    usbDeviceMusic.Title = musicItem.Title;
                                    usbDeviceMusic.Author = musicItem.Author;
                                    usbDeviceMusic.Album = musicItem.Album;
                                    usbDeviceMusic.Extension = musicItem.Extension;
                                    usbDeviceMusic.UniqueDeviceId = AppData.usbStorageDevice.UniqueId;
                                    AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                                }
                                ViewModel.RefreshUsbDeviceMusicList();
                            };
                            usbDeviceSubItem.Items.Add(menuItem);
                        }
                        menuFlyout.Items.Insert(4, usbDeviceSubItem);
                    }
                }
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                ViewModel.AlbumTextBlock_Tapped(textBlock);
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                ViewModel.AuthorTextBlock_Tapped(textBlock);
            }
        }

        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.MusicDetail_Click();
        }

        private void FlyoutAddToCurrentPlayList_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.AddToCurrentPlayList(uniqueSelectedMusics);
        }
    }
}
