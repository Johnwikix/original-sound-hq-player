using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    public sealed partial class AlbumPage : Page, ILibraryTab
    {
        private readonly ConnectedAnimationScope _coverAnimation;
        public AlbumViewModel ViewModel { get; }

        public AlbumPage()
        {
            ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
            this.InitializeComponent();
            _coverAnimation = new ConnectedAnimationScope(this, "AlbumCover");
            DataContext = this;
        }

        void ILibraryTab.Activate()
        {
            DetailView.ViewModel.IsClosingForTransition = false;
            ViewModel.ReceiveNavigation();
            Bindings.Initialize();
        }

        void ILibraryTab.Deactivate()
        {
            ViewModel.LeaveNavigation();
            _coverAnimation.Cancel();
            DetailView.ViewModel.IsClosingForTransition = false;
            // 缓存的网格仍需接收空 View，解除隐藏页对旧曲库分组的引用。
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
            if (DetailView.ViewModel.IsClosingForTransition) return;
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem)?.As<GridViewItem>();
            var coverBorder = FindCoverBorderInItem(item);
            bool animate = _coverAnimation.Prepare(coverBorder);
            SetEntryTransitions();
            ViewModel.AlbumGridView_ItemClick(sender, e);
            if (animate) _coverAnimation.StartDeferred(() => DetailView.DetailCoverBorder);
        }

        public void CollapseDetail()
        {
            if (!ViewModel.IsInDetailMode) return;
            var item = ViewModel.AppViewModel.CurrentAlbumObj is { } selected
                ? AlbumGridView.ContainerFromItem(selected)?.As<GridViewItem>() : null;
            var source = FindCoverBorderInItem(item);
            bool animate = source is not null && _coverAnimation.Prepare(DetailView.DetailCoverBorder);
            if (!animate) _coverAnimation.Cancel();
            SetExitTransitions();
            DetailView.ViewModel.IsClosingForTransition = animate;
            ViewModel.CollapseDetail();
            if (animate) _coverAnimation.StartDeferred(() => source, FinishCoverTransition);
        }

        private void FinishCoverTransition()
        {
            DetailView.ViewModel.IsClosingForTransition = false;
            DetailView.ViewModel.RefreshFromAppState();
        }

        private static Border? FindCoverBorderInItem(GridViewItem? item)
        {
            if (item is null) return null;
            return ToolUtils.FindVisualChild<Border>(item, b => b.Name == "CoverBorder");
        }

        public void EnterDetailFromCrossLink()
        {
            _coverAnimation.Cancel();
            DetailView.ViewModel.IsClosingForTransition = false;
            SetEntryTransitions();
            ViewModel.EnterDetailFromCrossLink();
        }
        public void RefreshDetailView() => ViewModel.RefreshDetailView();

        private void SetEntryTransitions()
        {
            DetailView.OpacityTransition = TransitionCache.Slow;
            BrowseZoom.OpacityTransition = TransitionCache.Fast;
        }

        private void SetExitTransitions()
        {
            DetailView.OpacityTransition = TransitionCache.Fast;
            BrowseZoom.OpacityTransition = TransitionCache.Slow;
        }
    }
}
