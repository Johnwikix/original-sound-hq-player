using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SongArtistListPage : Page, INavigatable
    {
        public SongArtistViewModel ViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private readonly ScrollerHelper _scrollHelper;
        private readonly Music?[] _selectedBuffer = new Music?[256];
        private Music? _pendingScroll;
        public SongArtistListPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SongArtistViewModel>(); ;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
            _musicDatabaseService = App.Services.GetRequiredService<MusicDatabaseService>(); ;
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

        public void OnScrollToMusic(Music selectedMusic)
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

        private void MusicListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            ViewModel.SelectedMusics.Clear();
            if (frameworkElement?.DataContext is not Music clickedItem)
            {
                e.Handled = true;
                return;
            }

            var selectedItems = MusicListView.SelectedItems;
            int n = selectedItems.Count;
            var buffer = _selectedBuffer.AsSpan();
            int written = 0;
            int clickedId = clickedItem.Id;
            bool isCurrentItemSelected = false;
            for (int i = 0; i < n && written < buffer.Length; i++)
            {
                if (selectedItems[i] is Music m)
                {
                    buffer[written++] = m;
                    if (m.Id == clickedId) isCurrentItemSelected = true;
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
                    var musicList = ViewModel.AppViewModel.ArtistSongs;
                    await _musicDatabaseService.AddMusicListToPlayList(musicList, playlist.Id);
                };
                PlayList.Items.Add(menuItem);
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
