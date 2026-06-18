using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.Controls
{
    public sealed partial class PlaylistDetailControl : UserControl
    {
        public PlaylistDetailViewModel ViewModel { get; }
        private readonly ScrollerHelper _scrollHelper;
        private readonly PlayListMusicItem?[] _selectedBuffer = new PlayListMusicItem?[256];
        private PlayListMusicItem? _pendingScroll;

        public static readonly DependencyProperty PlaySourceTagProperty =
            DependencyProperty.Register(
                nameof(PlaySourceTag),
                typeof(string),
                typeof(PlaylistDetailControl),
                new PropertyMetadata("PlayListSongsView"));

        public string PlaySourceTag
        {
            get => (string)GetValue(PlaySourceTagProperty);
            set => SetValue(PlaySourceTagProperty, value);
        }

        public PlaylistDetailControl()
        {
            ViewModel = App.Services.GetRequiredService<PlaylistDetailViewModel>();
            this.InitializeComponent();
            MusicListView.ContainerContentChanging += MusicListView_ContainerContentChanging;
            _scrollHelper = new ScrollerHelper(DispatcherQueue);
            _scrollHelper.Tick += OnScrollTick;
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.SetView(this);
            ViewModel.RefreshFromAppState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.SetView(null);
        }

        private void MusicListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
                AlbumCoverBehavior.ClearImagesInContainer(args.ItemContainer);
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

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private void MusicListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }

        private void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.MusicListView_DragItemsCompleted();
        }

        private void AuthorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is PlayListMusicItem plm)
            {
                ViewModel.AuthorTextBlock_Tapped(plm.Music.Author ?? string.Empty);
            }
        }

        private void AlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is PlayListMusicItem plm)
            {
                ViewModel.AlbumTextBlock_Tapped(plm.Music.Album ?? string.Empty);
            }
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

        private void EditPlaylistNameBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.CurrentPlayList is null) return;
            ViewModel.AppViewModel.EditPlayListName(ViewModel.AppViewModel.CurrentPlayList, () =>
                DialogHelper.ShowInputAsync(this.XamlRoot, "ModifyPlaylist", ViewModel.AppViewModel.CurrentPlayList.Name));
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
