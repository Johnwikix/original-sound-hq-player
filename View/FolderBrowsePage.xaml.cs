using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinRT;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    public sealed partial class FolderBrowsePage : Page, INavigatable
    {
        public FolderViewModel ViewModel { get; }

        public FolderBrowsePage()
        {
            ViewModel = App.Services.GetRequiredService<FolderViewModel>(); ;
            this.InitializeComponent();
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            DetailView.ViewModel.IsClosingForTransition = false;
            ViewModel.ReceiveNavigation();
        }

        private void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (DetailView.ViewModel.IsClosingForTransition) return;
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem)?.As<GridViewItem>();
            var coverBorder = FindCoverBorderInItem(item);
            if (coverBorder is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("FolderCover", coverBorder);
            }
            SetEntryTransitions();
            ViewModel.FolderGridView_ItemClick(sender, e);
            if (coverBorder is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("FolderCover");
                    anim?.TryStart(DetailView.DetailCoverBorder);
                });
            }
        }

        public void CollapseDetail()
        {
            if (!ViewModel.IsInDetailMode) return;
            var folder = ViewModel.AppViewModel.CurrentFolderObj;
            var detailBorder = DetailView.DetailCoverBorder;

            // 跨链进入(SongsList 行点 Artist/Album,主界面 bottom bar 跳转)时
            // 对应的 GridViewItem 可能不在视觉树里，此时不能触发 ConnectedAnimation。
            Border? sourceBorder = null;
            if (folder is not null)
            {
                var item = FolderGridView.ContainerFromItem(folder)?.As<GridViewItem>();
                sourceBorder = FindCoverBorderInItem(item);
            }
            bool canAnimate = detailBorder is not null && sourceBorder is not null;

            if (canAnimate)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("FolderCover", detailBorder);
            }
            SetExitTransitions();

            if (canAnimate)
            {
                DetailView.ViewModel.IsClosingForTransition = true;
                ViewModel.CollapseDetail();
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("FolderCover");
                    if (anim != null)
                    {
                        anim.Completed += (s, e) =>
                        {
                            DetailView.ViewModel.IsClosingForTransition = false;
                            DetailView.ViewModel.RefreshFromAppState();
                        };
                        if (!anim.TryStart(sourceBorder))
                        {
                            DetailView.ViewModel.IsClosingForTransition = false;
                            DetailView.ViewModel.RefreshFromAppState();
                        }
                    }
                    else
                    {
                        DetailView.ViewModel.IsClosingForTransition = false;
                        DetailView.ViewModel.RefreshFromAppState();
                    }
                });
            }
            else
            {
                ViewModel.CollapseDetail();
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

        private void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
        }

        public void EnterDetailFromCrossLink()
        {
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
