using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
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
    public sealed partial class PlayListSongPage : Page, INavigatable
    {
        public PlayListSongViewModel ViewModel { get; }
        public PlayListSongPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<PlayListSongViewModel>();
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            MusicListView.ContainerContentChanging += MusicListView_ContainerContentChanging;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        private void MusicListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
                WinUIMusicPlayer.Behaviors.AlbumCoverBehavior.ClearImagesInContainer(args.ItemContainer);
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

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            bool isCurrentItemSelected = false;
            ViewModel.SelectedMusics.Clear();
            if (frameworkElement?.DataContext is PlayListMusicItem clickedItem)
            {
                if (clickedItem is null) return;
                foreach (var item in MusicListView.SelectedItems)
                {
                    if (item is PlayListMusicItem selectedMusic)
                    {
                        ViewModel.SelectedMusics.Add(selectedMusic);
                        if (selectedMusic.Music.Id == clickedItem.Music.Id)
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

        private void EditPlaylistName_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AppViewModel.EditPlayListName(ViewModel.AppViewModel.CurrentPlayList, async () =>
            {
                ContentDialog contentDialog = new ContentDialog
                {
                    Title = ToolUtils.GetString("ModifyPlaylist"),
                    Content = new Microsoft.UI.Xaml.Controls.TextBox { Text = $"{ViewModel.AppViewModel.CurrentPlayList.Name}" },
                    PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                    CloseButtonText = ToolUtils.GetString("CloseButton"),
                    XamlRoot = this.XamlRoot
                };
                contentDialog.RequestedTheme = AppSettings.ElementTheme;
                ContentDialogResult result = await contentDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                    return textBox.Text;
                }
                return string.Empty;
            });
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
