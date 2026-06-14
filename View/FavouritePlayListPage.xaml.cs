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
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class FavouritePlayListPage : Page, INavigatable
    {
        public FavouritePlayListViewModel ViewModel { get; }
        private readonly ScrollerHelper _scrollHelper;
        private readonly Music?[] _selectedBuffer = new Music?[256];
        private Music? _pendingScroll;

        public FavouritePlayListPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<FavouritePlayListViewModel>();
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

        private async void MusicListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await ViewModel.DragItems();
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

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.ReceiveNavigation();
        }

        public void UpdateMusicListView()
        {
            ViewModel.UpdateMusicListView();
        }

        private void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            Music selectedMusic = MusicListView.SelectedItem as Music;
            ViewModel.MusicListView_DoubleTapped(selectedMusic);
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

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                ViewModel.AlbumTextBlock_Tapped(textBlock);
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                ViewModel.AuthorTextBlock_Tapped(textBlock);
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
