using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Foundation.Collections;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FavouritePlayListPage : Page, ILibraryTab
    {
        public FavouritePlayListViewModel ViewModel { get; }
        private readonly ListViewCentering _centering;
        private readonly PlaybackSelectionTracker _playbackSelection;
        private readonly Music?[] _selectedBuffer = new Music?[256];
        private bool _waitingForFirstFavoriteFrame;
        private bool _observedFirstFavoriteFrame;

        public FavouritePlayListPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<FavouritePlayListViewModel>();
            DataContext = this;
            MusicListView.DragItemsCompleted += MusicListView_DragItemsCompleted;
            _centering = new ListViewCentering(MusicListView);
            _playbackSelection = new PlaybackSelectionTracker(MusicListView, ViewModel.AppViewModel);
        }

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await ViewModel.DragItems();
        }

        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
            => await _centering.LocateAsync(ViewModel.LocateCurrentTrack());

        void ILibraryTab.Activate()
        {
            ViewModel.ReceiveNavigation();
            Bindings.Initialize();
            if (_observedFirstFavoriteFrame || _waitingForFirstFavoriteFrame) return;
            _waitingForFirstFavoriteFrame = true;
            WaitForFavoriteItems();
        }

        void ILibraryTab.Deactivate()
        {
            if (_waitingForFirstFavoriteFrame)
            {
                _waitingForFirstFavoriteFrame = false;
                StopWatchingFavoriteFrame();
            }
            _centering.Cancel();
            Bindings.StopTracking();
        }

        private void WaitForFavoriteItems()
        {
            MusicListView.Items.VectorChanged += OnFavoriteItemsChanged;
            WatchFavoriteLayoutIfPopulated();
        }

        private void OnFavoriteItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs args)
        {
            if (_waitingForFirstFavoriteFrame) WatchFavoriteLayoutIfPopulated();
        }

        private void WatchFavoriteLayoutIfPopulated()
        {
            MusicListView.LayoutUpdated -= OnFavoriteLayoutUpdated;
            if (MusicListView.Items.Count > 0)
                MusicListView.LayoutUpdated += OnFavoriteLayoutUpdated;
        }

        private void StopWatchingFavoriteFrame()
        {
            MusicListView.Items.VectorChanged -= OnFavoriteItemsChanged;
            MusicListView.LayoutUpdated -= OnFavoriteLayoutUpdated;
            CompositionTarget.Rendering -= OnFavoriteRendering;
        }

        private void OnFavoriteLayoutUpdated(object? sender, object e)
        {
            if (!_waitingForFirstFavoriteFrame || !ListViewInteraction.HasVisibleMusicRow(MusicListView)) return;
            MusicListView.Items.VectorChanged -= OnFavoriteItemsChanged;
            MusicListView.LayoutUpdated -= OnFavoriteLayoutUpdated;
            CompositionTarget.Rendering += OnFavoriteRendering;
        }

        private void OnFavoriteRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnFavoriteRendering;
            if (!_waitingForFirstFavoriteFrame) return;
            if (!ListViewInteraction.HasVisibleMusicRow(MusicListView))
            {
                WaitForFavoriteItems();
                return;
            }

            // 让首个有内容的收藏画面先提交，再安排后台比较器准备。
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PrepareSongTitleSort))
                _waitingForFirstFavoriteFrame = false;
        }

        private void PrepareSongTitleSort()
        {
            if (!_waitingForFirstFavoriteFrame) return;
            _waitingForFirstFavoriteFrame = false;
            _observedFirstFavoriteFrame = true;
            StopWatchingFavoriteFrame();
            ViewModel.AppViewModel.PrepareDefaultSongTitleSort();
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (ListViewInteraction.IsAction(e.OriginalSource as DependencyObject, MusicListView)) return;
            int index = ListViewInteraction.IndexFromSource(e.OriginalSource as DependencyObject, MusicListView);
            if (index < 0 || index >= MusicListView.Items.Count || MusicListView.Items[index] is not Music music) return;
            e.Handled = true;
            ViewModel.MusicListView_DoubleTapped(music);
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

        private void MusicRow_SongInvoked(object sender, Music music)
            => App.Services.GetRequiredService<LibraryNavigationService>().ShowSongDetails(music, (sender as MusicListRowControl)?.CoverElement, (sender as MusicListRowControl)?.TitleElement);

        private void MusicRow_ArtistInvoked(object sender, Music music)
            => ViewModel.BrowseViewModel.SelectBarArtist(music.Author);

        private void MusicRow_AlbumInvoked(object sender, Music music)
            => ViewModel.BrowseViewModel.SelectBarAlbum(music.Album);

    }
}
