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
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    public sealed partial class PlayListPage : Page, INavigatable
    {
        public PlayListViewModel ViewModel { get; }

        public PlayListPage()
        {
            ViewModel = App.Services.GetRequiredService<PlayListViewModel>();
            this.InitializeComponent();
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
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

        private void OnCoverPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            SetActionButtonsVisibility(grid, Visibility.Visible);
        }

        private void OnCoverPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            SetActionButtonsVisibility(grid, Visibility.Collapsed);
        }

        private static void SetActionButtonsVisibility(Grid grid, Visibility visibility)
        {
            if (grid.FindName("EditNameBtn") is Button editBtn) editBtn.Visibility = visibility;
            if (grid.FindName("ExportBtn") is Button exportBtn) exportBtn.Visibility = visibility;
            if (grid.FindName("RemoveBtn") is Button removeBtn) removeBtn.Visibility = visibility;
        }

        private void PlayListGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (DetailView.ViewModel.IsClosingForTransition) return;
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem).As<GridViewItem>();
            var coverBorder = FindCoverBorderInItem(item);
            if (coverBorder is not null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("PlaylistCover", coverBorder);
            }
            SetEntryTransitions();
            if (e.ClickedItem is PlayList playList)
            {
                ViewModel.EnterPlayList(playList);
            }
            if (coverBorder is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("PlaylistCover");
                    anim?.TryStart(DetailView.DetailCoverBorder);
                });
            }
        }

        private void PlayListGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        public void CollapseDetail()
        {
            if (!ViewModel.IsInDetailMode) return;
            var playList = ViewModel.AppViewModel.CurrentPlayList;
            var detailBorder = DetailView.DetailCoverBorder;

            Border? sourceBorder = null;
            if (playList is not null)
            {
                var item = PlayListGridView.ContainerFromItem(playList).As<GridViewItem>();
                sourceBorder = FindCoverBorderInItem(item);
            }
            bool canAnimate = detailBorder is not null && sourceBorder is not null;

            if (canAnimate)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("PlaylistCover", detailBorder);
            }
            SetExitTransitions();

            if (canAnimate)
            {
                DetailView.ViewModel.IsClosingForTransition = true;
                if (ViewModel.AppViewModel.CurrentPlayList is not null)
                {
                    ViewModel.AppViewModel.CurrentPlayList = null;
                }
                ViewModel.IsInDetailMode = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("PlaylistCover");
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
                if (ViewModel.AppViewModel.CurrentPlayList is not null)
                {
                    ViewModel.AppViewModel.CurrentPlayList = null;
                }
                ViewModel.IsInDetailMode = false;
            }
        }

        public void RefreshDetailView() { }

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

        private void SetEntryTransitions()
        {
            DetailView.OpacityTransition = TransitionCache.Slow;
            PlayListGrid.OpacityTransition = TransitionCache.Fast;
        }

        private void SetExitTransitions()
        {
            DetailView.OpacityTransition = TransitionCache.Fast;
            PlayListGrid.OpacityTransition = TransitionCache.Slow;
        }

        private async void RemovePlayListButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PlayList playList) return;
            if (await DialogHelper.ShowConfirmAsync(this.XamlRoot, "AreUSureDeletePlayList"))
            {
                await ViewModel.RemovePlayList(playList);
            }
        }

        private void EditPlayListNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PlayList playList) return;
            _ = ViewModel.AppViewModel.EditPlayListName(playList, () =>
                DialogHelper.ShowInputAsync(this.XamlRoot, "ModifyPlaylist", playList.Name));
        }

        private void ExportPlayList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                _ = ViewModel.ExportPlayList(playList);
            }
        }

        private async void AddPlayList_Click(object sender, RoutedEventArgs e)
        {
            var mainPage = App.Services.GetRequiredService<MainPage>();
            var playlistName = await mainPage.AddPlayListDialog.ShowAndGetNameAsync(this.XamlRoot);
            if (playlistName is not null)
            {
                PlayList newPlaylist = new() { Name = playlistName };
                await ViewModel.InsertPlayList(newPlaylist);
                ViewModel.AppViewModel.AllPlayList.Add(newPlaylist);
            }
        }
    }
}
