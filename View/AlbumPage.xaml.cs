using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    public sealed partial class AlbumPage : Page, INavigatable
    {
        public AlbumViewModel ViewModel { get; }

        public AlbumPage()
        {
            ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
            this.InitializeComponent();
            AlbumGridView.ContainerContentChanging += AlbumGridView_ContainerContentChanging;
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        private void AlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
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

        private void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem) as GridViewItem;
            var coverBorder = FindCoverBorderInItem(item);
            if (coverBorder is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("AlbumCover", coverBorder);
            }
            SetEntryTransitions();
            ViewModel.AlbumGridView_ItemClick(sender, e);
            if (coverBorder is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumCover");
                    anim?.TryStart(DetailView.DetailCoverBorder);
                });
            }
        }

        public void CollapseDetail()
        {
            if (!ViewModel.IsInDetailMode) return;
            var album = ViewModel.AppViewModel.CurrentAlbumObj;
            var detailBorder = DetailView.DetailCoverBorder;

            // 跨链进入(SongsList 行点 Artist/Album,主界面 bottom bar 跳转)时
            // 对应的 GridViewItem 可能不在视觉树里(virtualize 或根本不属于本页)，
            // 此时不能触发 ConnectedAnimation，否则 snapshot 找不到目的端会异常停留。
            Border? sourceBorder = null;
            if (album is not null)
            {
                var item = AlbumGridView.ContainerFromItem(album) as GridViewItem;
                sourceBorder = FindCoverBorderInItem(item);
            }
            bool canAnimate = detailBorder is not null && sourceBorder is not null;

            if (canAnimate)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("AlbumCover", detailBorder);
            }
            SetExitTransitions();
            ViewModel.CollapseDetail();
            if (canAnimate)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumCover");
                    anim?.TryStart(sourceBorder);
                });
            }
        }

        private static Border? FindCoverBorderInItem(GridViewItem? item)
        {
            if (item is null) return null;
            return FindVisualChild<Border>(item, b => b.Name == "CoverBorder");
        }

        private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
        {
            if (parent is null) return null;
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t && (match is null || match(t))) return t;
                var found = FindVisualChild(child, match);
                if (found is not null) return found;
            }
            return null;
        }

        public void EnterDetailFromCrossLink()
        {
            SetEntryTransitions();
            ViewModel.EnterDetailFromCrossLink();
        }
        public void RefreshDetailView() => ViewModel.RefreshDetailView();

        private void SetEntryTransitions()
        {
            DetailView.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };
            BrowseZoom.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(100) };
        }

        private void SetExitTransitions()
        {
            DetailView.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(100) };
            BrowseZoom.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };
        }
    }
}
