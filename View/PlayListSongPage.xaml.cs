using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
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


        private void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.MusicListView_DragItemsCompleted();
        }

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
        }

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
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

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }


        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.PlayMenuItem_Click(uniqueSelectedMusics);
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.DeleteMenuItem_Click(uniqueSelectedMusics);
        }

        private async void SetAsFavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            await ViewModel.SetAsFavoriteMenuItem_Click(uniqueSelectedMusics);
        }

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenInExplorer_Click();
        }

        private async void ConvertAudio_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            MenuFlyoutItem? menuItem = sender as MenuFlyoutItem;
            await ViewModel.ConvertAudio_Click(uniqueSelectedMusics, menuItem);
        }

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
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
                                    await usbWriter.WriteToUsb(uniqueSelectedMusics, usbDevice);
                                    foreach (var music in uniqueSelectedMusics)
                                    {
                                        var existingMusic = AppData.musicOnUsbDevice.AsValueEnumerable().Where(m => m.Title == music.Title).FirstOrDefault();
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
            IEnumerable<Music> uniqueSelectedMusics = GetUniqueSelectedItems();
            ViewModel.AddToCurrentPlayList(uniqueSelectedMusics);
        }

        private void EditPlaylistName_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.EditPlayListName(async () =>
            {
                ContentDialog contentDialog = new ContentDialog
                {
                    Title = ToolUtils.GetString("ModifyPlaylist"),
                    Content = new TextBox { Text = $"{ViewModel.PlayListName}" },
                    PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                    CloseButtonText = ToolUtils.GetString("CloseButton"),
                    XamlRoot = this.XamlRoot
                };
                contentDialog.RequestedTheme = AppSettings.elementTheme;
                ContentDialogResult result = await contentDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    TextBox textBox = (TextBox)contentDialog.Content;
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
    }
}
