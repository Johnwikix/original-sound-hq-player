using DevWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinUIMusicPlayer.Converters;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
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
    public sealed partial class SongFolderListPage : Page, INavigatable
    {
        public SongFolderListViewModel ViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        public SongFolderListPage(SongFolderListViewModel viewModel, MusicDatabaseService musicDatabaseService)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            _musicDatabaseService = musicDatabaseService;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
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

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
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

        private async void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            bool isCurrentItemSelected = false;
            ViewModel.SelectedMusics.Clear();
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                if (clickedItem is null) return;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is Music selectedMusic)
                    {
                        ViewModel.SelectedMusics.Add(selectedMusic);
                        if (selectedMusic.Id == clickedItem.Id)
                        {
                            isCurrentItemSelected = true;
                        }
                    }
                }
                if (!isCurrentItemSelected)
                {
                    MusicListView.SelectedItems.Clear();
                    ViewModel.SelectedMusics.Clear();
                    ViewModel.SelectedMusic = clickedItem;
                    ViewModel.SelectedMusics.Add(clickedItem);
                }
            }
            e.Handled = true;
        }

        private async void AddToPlayListBtn_Click(object sender, RoutedEventArgs e)
        {
            PlayList.Items.Clear();
            foreach (var playlist in ViewModel.AppViewModel.AllPlayList)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = playlist.Name
                };
                menuItem.Click += async (s, args) =>
                {
                    var musicList = ViewModel.AppViewModel.FolderSongs;
                    await _musicDatabaseService.AddMusicListToPlayList(musicList, playlist.Id);
                };
                PlayList.Items.Add(menuItem);
            }
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
