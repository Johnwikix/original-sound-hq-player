using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.Controls
{
    public sealed partial class MusicGroupDetailControl : UserControl
    {
        public MusicGroupDetailViewModel ViewModel { get; }
        private readonly ListViewCentering _centering;
        private readonly PlaybackSelectionTracker _playbackSelection;
        private readonly Music?[] _selectedBuffer = new Music?[256];

        public static readonly DependencyProperty PlaySourceTagProperty =
            DependencyProperty.Register(
                nameof(PlaySourceTag),
                typeof(string),
                typeof(MusicGroupDetailControl),
                new PropertyMetadata("SongsSourceView"));

        public string PlaySourceTag
        {
            get => (string)GetValue(PlaySourceTagProperty);
            set => SetValue(PlaySourceTagProperty, value);
        }

        public static readonly DependencyProperty DetailKindProperty =
            DependencyProperty.Register(
                nameof(DetailKind),
                typeof(MusicGroupDetailViewModel.GroupKind),
                typeof(MusicGroupDetailControl),
                new PropertyMetadata(MusicGroupDetailViewModel.GroupKind.None));

        public MusicGroupDetailViewModel.GroupKind DetailKind
        {
            get => (MusicGroupDetailViewModel.GroupKind)GetValue(DetailKindProperty);
            set => SetValue(DetailKindProperty, value);
        }

        public MusicGroupDetailControl()
        {
            // 控件拥有此实例；避免根 DI 容器长期保留 transient IDisposable。
            ViewModel = new MusicGroupDetailViewModel(
                App.Services.GetRequiredService<MusicBrowseViewModel>(),
                App.Services.GetRequiredService<AppViewModel>(),
                App.Services.GetRequiredService<MusicDatabaseService>(),
                App.Services.GetRequiredService<ILogger<MusicGroupDetailViewModel>>(),
                App.Services.GetRequiredService<ApplicationTasks>());
            this.InitializeComponent();
            _centering = new ListViewCentering(MusicListView);
            _playbackSelection = new PlaybackSelectionTracker(MusicListView, ViewModel.AppViewModel);
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Activate();
            ViewModel.SetPageKind(DetailKind);
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
            if (ListViewInteraction.IsAction(e.OriginalSource as DependencyObject, MusicListView)) return;
            ViewModel.MusicListView_DoubleTapped();
        }

        private void MusicRow_SongInvoked(object sender, Music music)
            => App.Services.GetRequiredService<LibraryNavigationService>().ShowSongDetails(music, (sender as MusicListRowControl)?.CoverElement, (sender as MusicListRowControl)?.TitleElement);

        private void MusicRow_ArtistInvoked(object sender, Music music)
            => ViewModel.AuthorTextBlock_Tapped(music.Author);

        private void MusicRow_AlbumInvoked(object sender, Music music)
            => ViewModel.AlbumTextBlock_Tapped(music.Album);

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
            var db = App.Services.GetRequiredService<MusicDatabaseService>();
            var list = ViewModel.Songs;
            foreach (var playlist in ViewModel.AppViewModel.AllPlayList)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = playlist.Name
                };
                menuItem.Click += async (s, args) =>
                {
                    await db.AddMusicListToPlayList(list, playlist.Id);
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
