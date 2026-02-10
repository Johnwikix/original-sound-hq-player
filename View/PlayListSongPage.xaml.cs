using DevWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Converters;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PlayListSongPage : Page, INavigatable
    {
        public PlayListSongViewModel ViewModel { get; }
        public PlayListSongPage(PlayListSongViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        public void OnScrollToMusic(PlayListMusicItem selectedMusic)
        {
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MusicListView.ScrollIntoView(selectedMusic);
                });
            });
        }


        private void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.MusicListView_DragItemsCompleted();
        }

        //public void SortMusicList(string sortOrder)
        //{
        //    ViewModel.SortMusicList(sortOrder);
        //}

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private void ReGetLyrics_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ReGetLyrics_Click(GetUniqueSelectedItems());
        }
        private IEnumerable<PlayListMusicItem> GetUniqueSelectedItems()
        {
            var selectedItems = MusicListView.SelectedItems;
            foreach (var item in selectedItems)
            {
                if (item is PlayListMusicItem music)
                {
                    yield return music;
                }
            }
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }


        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.PlayMenuItem_Click(GetUniqueSelectedItems());
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.DeleteMenuItem_Click(GetUniqueSelectedItems());
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SetAsFavoriteMenuItem_Click(GetUniqueSelectedItems());
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenInExplorer_Click();
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            MenuFlyoutItem? menuItem = sender as MenuFlyoutItem;
            await ViewModel.ConvertAudio_Click(GetUniqueSelectedItems(), menuItem);
        }

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var targetElement = e.OriginalSource as FrameworkElement;
            ListViewItem listViewItem = ToolUtils.FindParent<ListViewItem>(targetElement);
            if (listViewItem is not null)
            {
                var musicItem = listViewItem.Content as PlayListMusicItem;
                // 检查当前指向的元素是否已在选中项列表中
                bool isCurrentItemSelected = false;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is PlayListMusicItem selectedMusic && musicItem is not null && selectedMusic.Music == musicItem.Music)
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
                IEnumerable<PlayListMusicItem> uniqueSelectedMusics = GetUniqueSelectedItems();
                // 设置右键菜单
                if (listViewItem.ContextFlyout is MenuFlyout flyout && musicItem is not null)
                {
                    // 为菜单项设置DataContext
                    foreach (var menuItem in flyout.Items)
                    {
                        menuItem.DataContext = musicItem;
                    }
                    if (menuFlyout.Items.Count > 8)
                    {
                        MenuFlyoutSubItem fifthItem = menuFlyout.Items[3] as MenuFlyoutSubItem;
                        if (fifthItem is not null)
                        {
                            if (fifthItem.Tag.ToString() == "usbDevice")
                            {
                                menuFlyout.Items.RemoveAt(3);
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
                                if (uniqueSelectedMusics.AsValueEnumerable().Count() > 1)
                                {
                                    ViewModel.ShowTransmission();
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        ViewModel.HideTransmission();
                                    };
                                    await usbWriter.WriteToUsb(uniqueSelectedMusics.Select(x=>x.Music), usbDevice);
                                    foreach (var music in uniqueSelectedMusics)
                                    {
                                        var existingMusic = AppData.musicOnUsbDevice.AsValueEnumerable().Where(m => m.Title == music.Music.Title).FirstOrDefault();
                                        if (existingMusic is not null)
                                        {
                                            continue; // 如果已经存在，则跳过
                                        }
                                        UsbDeviceMusic usbDeviceMusic = new UsbDeviceMusic();
                                        usbDeviceMusic.Title = music.Music.Title;
                                        usbDeviceMusic.Author = music.Music.Author;
                                        usbDeviceMusic.Album = music.Music.Album;
                                        usbDeviceMusic.Extension = music.Music.Extension;
                                        usbDeviceMusic.UniqueDeviceId = AppData.usbStorageDevice.UniqueId;
                                        AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                                    }
                                }
                                else if (musicItem is not null)
                                {
                                    ViewModel.ShowTransmission();
                                    List<PlayListMusicItem> musicItems = new List<PlayListMusicItem> { musicItem };
                                    var usbWriter = new UsbWriterHelper();
                                    usbWriter.hideTransmission += (sender, args) =>
                                    {
                                        ViewModel.HideTransmission();
                                    };
                                    await usbWriter.WriteToUsb(musicItems.Select(x=>x.Music), usbDevice);
                                    UsbDeviceMusic usbDeviceMusic = new UsbDeviceMusic();
                                    usbDeviceMusic.Title = musicItem.Music.Title;
                                    usbDeviceMusic.Author = musicItem.Music.Author;
                                    usbDeviceMusic.Album = musicItem.Music.Album;
                                    usbDeviceMusic.Extension = musicItem.Music.Extension;
                                    usbDeviceMusic.UniqueDeviceId = AppData.usbStorageDevice.UniqueId;
                                    AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                                }
                                //ViewModel.RefreshUsbDeviceMusicList(null, null);
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
                ViewModel.AlbumTextBlock_Tapped(albumName);
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
        private void MusicDetail_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.MusicDetail_Click();
        }

        private void FlyoutAddToCurrentPlayList_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AppObservableObj.AddToCurrentPlayList(GetUniqueSelectedItems().Select(x=>x.Music));
        }

        private void EditPlaylistName_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.EditPlayListName(async () =>
            {
                ContentDialog contentDialog = new ContentDialog
                {
                    Title = ToolUtils.GetString("ModifyPlaylist"),
                    Content = new Microsoft.UI.Xaml.Controls.TextBox { Text = $"{ViewModel.AppObservableObj.CurrentPlayList.Name}" },
                    PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                    CloseButtonText = ToolUtils.GetString("CloseButton"),
                    XamlRoot = this.XamlRoot
                };
                contentDialog.RequestedTheme = AppSettings.elementTheme;
                ContentDialogResult result = await contentDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                    return textBox.Text;
                }
                return string.Empty;
            });
        }

        private void MusicListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.Item is Music music)
                {
                    AlbumArtConverter.OnMusicUnloaded(music.Id);
                }
            }
        }

        private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = true;
            }
        }

        private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }
    }
}
