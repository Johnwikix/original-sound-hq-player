using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
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
    public sealed partial class SongListPage : Page, ILibraryTab
    {
        public SongListViewModel ViewModel { get; }
        private readonly ListViewCentering _centering;
        private readonly PlaybackSelectionTracker _playbackSelection;
        private readonly Music?[] _selectedBuffer = new Music?[256];

        public SongListPage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SongListViewModel>();
            DataContext = this;
            _centering = new ListViewCentering(MusicListView);
            _playbackSelection = new PlaybackSelectionTracker(MusicListView, ViewModel.AppViewModel);
        }

        void ILibraryTab.Activate()
        {
            ViewModel.ReceiveNavigation();
            Bindings.Initialize();
        }

        void ILibraryTab.Deactivate()
        {
            _centering.Cancel();
            Bindings.StopTracking();
        }

        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
            => await _centering.LocateAsync(ViewModel.LocateCurrentTrack());

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
            => ViewModel.AuthorTextBlock_Tapped(music.Author);

        private void MusicRow_AlbumInvoked(object sender, Music music)
            => ViewModel.AlbumTextBlock_Tapped(music.Album);

    }
}
