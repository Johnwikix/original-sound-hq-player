using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.View;

/// <summary>主外壳拥有的详情展示、返回状态与跨链导航，集中管理导航交接及迟到请求。</summary>
public sealed partial class MainPage
{
    private MusicDetailsRequest? _pendingMusicDetails;
    private bool _returnPlayingAfterDetails;
    private bool _backBeforeMusicDetails;
    private readonly MusicDetailsPresenter _detailsPresenter;

    private void DismissMusicDetailsForNavigation()
    {
        _pendingMusicDetails = null;
        _returnPlayingAfterDetails = false;
        if (!_detailsPresenter.IsOpen) return;
        _detailsPresenter.Dismiss();
        // 先还原来源状态，随后目标页的导航负责发布自己的返回可用性。
        ViewModel.AppViewModel.IsBackBtnEnable = _backBeforeMusicDetails;
    }

    private void NavigateTo(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
    {
        DismissMusicDetailsForNavigation();
        if (pageType == typeof(HomePage))
        {
            AppData.CurrentPage = typeof(HomePage);
            ViewModel.AppViewModel.IsBackBtnEnable = false;
        }
        MainFrame.Navigate(pageType, parameter, navigationTransitionInfo);
        if (MainFrame.Content is PlayListPage playlist)
            ViewModel.AppViewModel.IsBackBtnEnable = playlist.ViewModel.IsInDetailMode;
        MainFrame.BackStack.Clear();
    }

    public void NavigateToMusicBrowsePage()
    {
        // 即便主 Frame 已在曲库，底栏仍可能要切换内层 Frame；先释放临时详情及旧返回回调。
        DismissMusicDetailsForNavigation();
        if (MainFrame.Content is not MusicBrowsePage)
        {
            foreach (var item in NavigationViewControl.MenuItems)
                if (item is NavigationViewItem navigationItem && navigationItem.Tag?.ToString() == "MusicBrowse")
                {
                    NavigationViewControl.SelectedItem = navigationItem;
                    break;
                }
            NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
        }
    }

    public void ShowMusicDetails(MusicDetailsRequest request, FrameworkElement? sourceCover = null, FrameworkElement? sourceTitle = null)
    {
        if (!ViewModel.AppViewModel.CanPublishState) return;
        CurrentPlayListTeachingTip.IsOpen = false;
        if (PlayingFrame.Content is PlayingDetailPage playing) playing.CloseQueuePopup();
        if (_isPageTransitioning)
        {
            _pendingMusicDetails = request;
            return;
        }
        if (PlayingFrame.Visibility == Visibility.Visible)
        {
            _returnPlayingAfterDetails = true;
            _pendingMusicDetails = request;
            NavigatebackToMusicBrowsePage();
            return;
        }
        if (!_detailsPresenter.IsOpen) _backBeforeMusicDetails = ViewModel.AppViewModel.IsBackBtnEnable;
        if (!_detailsPresenter.Show(request, sourceCover, sourceTitle))
        {
            ViewModel.AppViewModel.IsBackBtnEnable = _backBeforeMusicDetails;
            if (_returnPlayingAfterDetails)
            {
                _returnPlayingAfterDetails = false;
                NavigateToPlayingDetailPage();
            }
            return;
        }
        ViewModel.AppViewModel.IsBackBtnEnable = true;
    }

    private void OpenPendingDetails()
    {
        if (_pendingMusicDetails is not { } request) return;
        _pendingMusicDetails = null;
        ShowMusicDetails(request);
    }

    public void CloseMusicDetails()
    {
        if (!_detailsPresenter.IsOpen) return;
        bool showPlaying = _returnPlayingAfterDetails;
        _returnPlayingAfterDetails = false;
        _detailsPresenter.Back(() =>
        {
            ViewModel.AppViewModel.IsBackBtnEnable = _backBeforeMusicDetails;
            if (showPlaying) NavigateToPlayingDetailPage();
        });
    }
}
