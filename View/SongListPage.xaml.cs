using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SongListPage : Page, INavigatable
    {
        public SongListViewModel ViewModel { get; }
        private readonly ListViewCentering _centering;
        private readonly Music?[] _selectedBuffer = new Music?[256];

        public SongListPage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SongListViewModel>();
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            _centering = new ListViewCentering(MusicListView);
            Unloaded += OnUnloaded;
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

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _centering.Cancel();
            base.OnNavigatedFrom(e);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => _centering.Cancel();

        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
            => await _centering.LocateAsync(ViewModel.LocateCurrentTrack());

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ViewModel.MusicListView_DoubleTapped();
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

        private void MusicRow_ArtistInvoked(object sender, Music music)
            => ViewModel.AuthorTextBlock_Tapped(music.Author);

        private void MusicRow_AlbumInvoked(object sender, Music music)
            => ViewModel.AlbumTextBlock_Tapped(music.Album);

    }
}
