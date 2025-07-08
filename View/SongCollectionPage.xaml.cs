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
    public sealed partial class SongCollectionPage : Page, INavigatable
    {
        public SongCollectionViewModel ViewModel { get; }
        //private readonly IMessenger _messenger;
        public SongCollectionPage(SongCollectionViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            //_messenger = messenger;
            //_messenger.Register<ScrollToMusicMessageHepler>(this, OnScrollToMusic);
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        // 然后在 SongListPage 的 OnNavigatedTo 中接收参数
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.ReceiveNavigation();
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

        public void SortMusicList(string sortOrder, string type)
        {
            ViewModel.SortMusicList(sortOrder, type);            
        }       

        public void UpdateFavouriteMusic(Music music)
        {
            ViewModel.UpdateFavouriteMusic(music);            
        }

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();            
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();            
        }

        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.PlayMenuItem_Click(uniqueSelectedMusics);            
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.DeleteMenuItem_Click(uniqueSelectedMusics);            
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.SetAsFavoriteMenuItem_Click(uniqueSelectedMusics);            
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenInExplorer_Click();            
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            List<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            MenuFlyoutItem? menuItem = sender as MenuFlyoutItem;
            await ViewModel.ConvertAudio_Click(uniqueSelectedMusics, menuItem);            
        }

        private async void IsFavouriteIconButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is Music music)
            {
                await ViewModel.IsFavouriteIconButton_Click(music);                
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                ViewModel.AuthorTextBlock_Tapped(artist);                
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                ViewModel.AlbumTextBlock_Tapped(albumName);                
            }
        }

        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.MusicDetail_Click();           
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
                        menuFlyout.Items.Insert(4, usbDeviceSubItem);
                    }
                }
            }
        }
    }
}
