using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
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
        private readonly ConnectedAnimationScope _coverAnimation;
        public PlayListViewModel ViewModel { get; }
        private readonly HashSet<Grid> _hoveredPlaylistCards = [];

        public PlayListPage()
        {
            ViewModel = App.Services.GetRequiredService<PlayListViewModel>();
            this.InitializeComponent();
            _coverAnimation = new ConnectedAnimationScope(this, "PlaylistCover");
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            PlayListGridView.SelectionChanged += PlayListGridView_SelectionChanged;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _coverAnimation.Cancel();
            DetailView.ViewModel.IsClosingForTransition = false;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            PlayListGridView.SelectionChanged -= PlayListGridView_SelectionChanged;
            _hoveredPlaylistCards.Clear();
            Unloaded -= OnUnloaded;
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayListViewModel.IsEditMode))
            {
                ApplyEditMode();
            }
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
            ViewModel.SetEditMode(false);
            ApplyEditMode();
        }

        private void ApplyEditMode()
        {
            bool isEditMode = ViewModel.IsEditMode;
            if (!isEditMode)
            {
                // WinUI does not allow mutating SelectedItems while SelectionMode=None.
                // Clear the vector before switching modes; on first navigation it is already None.
                if (PlayListGridView.SelectionMode != ListViewSelectionMode.None
                    && PlayListGridView.SelectedItems.Count > 0)
                {
                    PlayListGridView.SelectedItems.Clear();
                }
                ViewModel.ClearSelectedPlayLists();
            }

            PlayListGridView.SelectionMode = isEditMode
                ? ListViewSelectionMode.Extended
                : ListViewSelectionMode.None;

            foreach (var grid in _hoveredPlaylistCards)
            {
                SetActionButtonsVisibility(grid, isEditMode ? Visibility.Collapsed : Visibility.Visible);
            }
        }

        private void PlayListGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ViewModel.IsEditMode) return;
            var selected = new List<PlayList>(PlayListGridView.SelectedItems.Count);
            foreach (var item in PlayListGridView.SelectedItems)
            {
                if (item is PlayList playList) selected.Add(playList);
            }
            ViewModel.SetSelectedPlayLists(selected);
        }

        private void OnCoverPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            _hoveredPlaylistCards.Add(grid);
            SetActionButtonsVisibility(grid, Visibility.Visible);
        }

        private void OnCoverPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            _hoveredPlaylistCards.Remove(grid);
            SetActionButtonsVisibility(grid, Visibility.Collapsed);
        }

        private void SetActionButtonsVisibility(Grid grid, Visibility visibility)
        {
            Visibility actionVisibility = ViewModel.IsEditMode ? Visibility.Collapsed : visibility;
            if (grid.FindName("PlayBtn") is Button playBtn) playBtn.Visibility = actionVisibility;
            if (grid.FindName("MoreBtn") is Button moreBtn) moreBtn.Visibility = actionVisibility;
        }

        private void PlayListGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (ViewModel.IsEditMode) return;
            if (DetailView.ViewModel.IsClosingForTransition) return;
            var gridView = sender as GridView;
            var item = gridView?.ContainerFromItem(e.ClickedItem)?.As<GridViewItem>();
            var coverBorder = FindCoverBorderInItem(item);
            bool animate = _coverAnimation.Prepare(coverBorder);
            SetEntryTransitions();
            if (e.ClickedItem is PlayList playList)
            {
                ViewModel.EnterPlayList(playList);
            }
            if (animate) _coverAnimation.StartDeferred(() => DetailView.DetailCoverBorder);
        }

        private void PlayListGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        public void CollapseDetail()
        {
            if (!ViewModel.IsInDetailMode) return;
            var item = ViewModel.AppViewModel.CurrentPlayList is { } selected
                ? PlayListGridView.ContainerFromItem(selected)?.As<GridViewItem>() : null;
            var source = FindCoverBorderInItem(item);
            bool animate = source is not null && _coverAnimation.Prepare(DetailView.DetailCoverBorder);
            if (!animate) _coverAnimation.Cancel();
            SetExitTransitions();
            DetailView.ViewModel.IsClosingForTransition = animate;
            if (ViewModel.AppViewModel.CurrentPlayList is not null) ViewModel.AppViewModel.CurrentPlayList = null;
            ViewModel.IsInDetailMode = false;
            if (animate) _coverAnimation.StartDeferred(() => source, FinishCoverTransition);
        }

        private void FinishCoverTransition()
        {
            DetailView.ViewModel.IsClosingForTransition = false;
            DetailView.ViewModel.RefreshFromAppState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _coverAnimation.Cancel();
            DetailView.ViewModel.IsClosingForTransition = false;
            base.OnNavigatedFrom(e);
        }

        public void RefreshDetailView() { }

        private static Border? FindCoverBorderInItem(GridViewItem? item)
        {
            if (item is null) return null;
            return ToolUtils.FindVisualChild<Border>(item, b => b.Name == "CoverBorder");
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

        private void PlayPlayList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                _ = ViewModel.PlayPlayList(playList);
            }
        }

        private void EditModeButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetEditMode(!ViewModel.IsEditMode);
        }

        private void SelectAllPlayListsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsEditMode) return;
            PlayListGridView.SelectedItems.Clear();
            foreach (var playList in ViewModel.AppViewModel.AllPlayList)
            {
                PlayListGridView.SelectedItems.Add(playList);
            }
        }

        private async void DeleteSelectedPlayListsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanDeleteSelectedPlayLists) return;
            if (!await DialogHelper.ShowConfirmAsync(this.XamlRoot, "AreUSureDeletePlayLists")) return;

            try
            {
                await ViewModel.DeleteSelectedPlayListsAsync();
            }
            catch (Exception ex)
            {
                ReportPlaylistOperationError(ex, "批量删除播放列表失败");
            }
        }

        private async void PlayListGridView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (!ViewModel.IsEditMode) return;
            try
            {
                await ViewModel.PersistPlayListOrderAsync();
            }
            catch (Exception ex)
            {
                ReportPlaylistOperationError(ex, "保存播放列表排序失败");
            }
        }

        private void ReportPlaylistOperationError(Exception exception, string operation)
        {
            App.GetLogger<PlayListPage>().LogError(exception, operation);
            ViewModel.AppViewModel.InfoBarTitle = ToolUtils.GetString("Error");
            ViewModel.AppViewModel.InfoBarMessage = exception.Message;
            ViewModel.AppViewModel.InfoBarIsOpen = true;
        }

        private async void RemovePlayListButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not PlayList playList) return;
            if (await DialogHelper.ShowConfirmAsync(this.XamlRoot, "AreUSureDeletePlayList"))
            {
                try
                {
                    await ViewModel.RemovePlayList(playList);
                }
                catch (Exception ex)
                {
                    ReportPlaylistOperationError(ex, "删除播放列表失败");
                }
            }
        }

        private void EditPlayListNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not PlayList playList) return;
            _ = ViewModel.AppViewModel.EditPlayListName(playList, () =>
                DialogHelper.ShowInputAsync(this.XamlRoot, "ModifyPlaylist", playList.Name));
        }

        private void ExportPlayList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is PlayList playList)
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
