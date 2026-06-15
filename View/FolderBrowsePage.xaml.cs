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
    public sealed partial class FolderBrowsePage : Page, INavigatable
    {
        public FolderViewModel ViewModel { get; }

        public FolderBrowsePage()
        {
            ViewModel = App.Services.GetRequiredService<FolderViewModel>(); ;
            this.InitializeComponent();
            FolderGridView.ContainerContentChanging += FolderGridView_ContainerContentChanging;
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        private void FolderGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
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

        private void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem) as GridViewItem;
            var coverBorder = FindCoverBorderInItem(item);
            if (coverBorder is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("FolderCover", coverBorder);
            }
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
            if (detailBorder is not null && folder is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("FolderCover", detailBorder);
            }
            ViewModel.CollapseDetail();
            if (detailBorder is not null && folder is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("FolderCover");
                    if (anim is null) return;
                    var item = FolderGridView.ContainerFromItem(folder) as GridViewItem;
                    var sourceBorder = FindCoverBorderInItem(item);
                    if (sourceBorder is not null)
                    {
                        anim.TryStart(sourceBorder);
                    }
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

        private void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var frameworkElement = e.OriginalSource as FrameworkElement;
            if (frameworkElement?.DataContext is Music clickedItem)
            {
                ViewModel.SelectedItem = clickedItem;
            }
            e.Handled = true;
        }

        public void EnterDetailFromCrossLink() => ViewModel.EnterDetailFromCrossLink();
        public void RefreshDetailView() => ViewModel.RefreshDetailView();
    }
}
