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
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.Controls
{
    public sealed partial class PlaylistDetailControl : UserControl
    {
        public PlaylistDetailViewModel ViewModel { get; }
        private readonly ListViewCentering _centering;
        private readonly PlayListMusicItem?[] _selectedBuffer = new PlayListMusicItem?[256];

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
            _centering = new ListViewCentering(MusicListView);
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Activate();
            ViewModel.RefreshFromAppState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _centering.Cancel();
            ViewModel.Dispose();
        }

        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
            => await _centering.LocateAsync(ViewModel.LocateCurrentTrack());

        private void MusicListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }

        private void MusicRow_ArtistInvoked(object sender, Music music)
            => ViewModel.AuthorTextBlock_Tapped(music.Author);

        private void MusicRow_AlbumInvoked(object sender, Music music)
            => ViewModel.AlbumTextBlock_Tapped(music.Album);

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await ViewModel.MusicListView_DragItemsCompleted();
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
            _ = ViewModel.AppViewModel.EditPlayListName(ViewModel.AppViewModel.CurrentPlayList, () =>
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
