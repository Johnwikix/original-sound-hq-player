using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Extensions;
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
        private readonly ScrollerHelper _scrollHelper;
        private readonly PlayListMusicItem?[] _selectedBuffer = new PlayListMusicItem?[256];
        private PlayListMusicItem? _pendingScroll;
        public PlayListSongPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<PlayListSongViewModel>();
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            MusicListView.ContainerContentChanging += MusicListView_ContainerContentChanging;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            _scrollHelper = new ScrollerHelper(DispatcherQueue);
            _scrollHelper.Tick += OnScrollTick;
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
            _pendingScroll = selectedMusic;
            _scrollHelper.Trigger();
        }

        private void OnScrollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (_pendingScroll is { } m)
            {
                MusicListView.ScrollIntoView(m);
                _pendingScroll = null;
            }
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
            ViewModel.SelectedMusics.Clear();
            if (frameworkElement?.DataContext is not PlayListMusicItem clickedItem)
            {
                e.Handled = true;
                return;
            }

            var selectedItems = MusicListView.SelectedItems;
            int n = selectedItems.Count;
            var buffer = _selectedBuffer.AsSpan();
            int written = 0;
            int clickedMusicId = clickedItem.Music.Id;
            bool isCurrentItemSelected = false;
            for (int i = 0; i < n && written < buffer.Length; i++)
            {
                if (selectedItems[i] is PlayListMusicItem plm)
                {
                    buffer[written++] = plm;
                    if (plm.Music.Id == clickedMusicId) isCurrentItemSelected = true;
                }
            }

            if (!isCurrentItemSelected)
            {
                selectedItems.Clear();
                ViewModel.SelectedMusic = clickedItem;
                ViewModel.SelectedMusics.Add(clickedItem);
            }
            else
            {
                for (int i = 0; i < written; i++)
                {
                    if (buffer[i] is { } sel) ViewModel.SelectedMusics.Add(sel);
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
                    Content = new Microsoft.UI.Xaml.Controls.TextBox { Text = ViewModel.AppViewModel.CurrentPlayList.Name },
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
