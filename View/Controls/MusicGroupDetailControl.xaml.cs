using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.Controls
{
    public sealed partial class MusicGroupDetailControl : UserControl
    {
        public MusicGroupDetailViewModel ViewModel { get; }
        private readonly ScrollerHelper _scrollHelper;
        private readonly Music?[] _selectedBuffer = new Music?[256];
        private Music? _pendingScroll;

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

        public MusicGroupDetailControl()
        {
            ViewModel = App.Services.GetRequiredService<MusicGroupDetailViewModel>();
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

        private void MusicListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
        }

        private void AuthorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Music music)
            {
                ViewModel.AuthorTextBlock_Tapped(music.Author ?? string.Empty);
            }
        }

        private void AlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Music music)
            {
                ViewModel.AlbumTextBlock_Tapped(music.Album ?? string.Empty);
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
