using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    public enum TitleBarArea { None, Top }

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }
        public EqualizerDialog EqualizerDialog { get; set; }
        public SettingsDialog SettingsDialog { get; set; }
        public AddPlayListDialog AddPlayListDialog { get; set; }
        /// <summary>系统文本缩放绑定根：ActualHeight 无变更通知，经此 INPC 服务随系统设置联动。</summary>
        public TextScaleService TextScale => TextScaleService.Instance;
        private readonly INavigationService _playingNavigation;
        private bool _isPageTransitioning = false;
        private MusicDetailsRequest? _pendingMusicDetails;
        private bool _returnPlayingAfterDetails;
        private bool _backBeforeMusicDetails;
        private readonly MusicDetailsPresenter _detailsPresenter;

        private readonly ListViewCentering _queueCentering;
        private readonly PlaybackSelectionTracker _playbackSelection;
        internal MusicBrowsePage? CurrentLibraryPage => MainFrame.Content as MusicBrowsePage;

        public bool IsPlayingDetailVisible => PlayingFrame.Visibility == Visibility.Visible;
        //private ToolTip _progressToolTip = new();
        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            ViewModel = viewModel;
            DataContext = this;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _playingNavigation = navigationServiceFactory.CreateNavigationService(PlayingFrame);
            _playingNavigation.RegisterPage<PlayingDetailPage>();
            _detailsPresenter = new MusicDetailsPresenter(MainFrame);
            _queueCentering = new ListViewCentering(CurrentPlayListView);
            _playbackSelection = new PlaybackSelectionTracker(CurrentPlayListView, ViewModel.AppViewModel, queue: true);
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        private void DesktopLyricsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.DesktopLyrics.IsEnabled = !ViewModel.DesktopLyrics.IsEnabled;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            NavigationViewControl.IsPaneOpen = false;
            NavigateToDefaultPage();
            InitiaizeEqualizerDialog();
            SetSettingsDialog();
            AddPlayListDialog ??= new AddPlayListDialog(ViewModel.AppViewModel);
            NavigationViewControl.Visibility = Visibility.Visible;
            if (App.MainWindow is { } mainWindow)
            {
                mainWindow.AppWindow.Changed += MainPage_AppWindow_Changed;
                AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
                SetTitleBarArea(TitleBarArea.Top);
            }
            ViewModel.AppViewModel.UpdateMaximizeState();
            Loaded -= MainPage_Loaded;
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _queueCentering.Cancel();
            try
            {
                if (App.MainWindow is { } mw)
                    mw.AppWindow.Changed -= MainPage_AppWindow_Changed;
            }
            catch
            {
            }
            Unloaded -= MainPage_Unloaded;
        }

        private void MainPage_AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {            
            if (args.DidPresenterChange)
            {
                ViewModel.AppViewModel.SyncFullScreenStateFromWindow();
            }
            if (args.DidPositionChange || args.DidSizeChange)
            {
                ViewModel.AppViewModel.UpdateMaximizeState();
                SetTitleBarArea(TitleBarArea.Top);
            }
        }

        private void AppTitleBar_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.AppViewModel.IsPlayingDetailVisible) return;
            ViewModel.AppViewModel.IsPointerOverTitleBar = true;
        }

        private void AppTitleBar_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.AppViewModel.IsPlayingDetailVisible) return;
            ViewModel.AppViewModel.IsPointerOverTitleBar = false;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.Minimize();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            var window = App.MainWindow;
            if (window?.AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                if (overlapped.State == OverlappedPresenterState.Maximized)
                    window.Restore();
                else
                    window.Maximize();
            }
            ViewModel.AppViewModel.UpdateMaximizeState();
        }

        private void FullscreenTitleBarButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AppViewModel.ToggleFullScreen();
        }

        private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTitleBarArea(TitleBarArea.Top);
        }

        private static readonly RectInt32[] _dragRectNone = [new(0, 0, 0, 0)];
        private static readonly RectInt32[] _dragRectTop = [new()];

        private void SetTitleBarArea(TitleBarArea titleBarArea)
        {
            if (App.MainWindow?.AppWindow?.TitleBar is null) return;

            var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;

            switch (titleBarArea)
            {
                case TitleBarArea.None:
                    App.MainWindow.AppWindow.TitleBar.SetDragRectangles(_dragRectNone);
                    break;

                case TitleBarArea.Top:
                    _dragRectTop[0] = new RectInt32(
                        0, 0,
                        (int)(AppTitleBar.ActualWidth * scale),
                        (int)(AppTitleBar.ActualHeight * scale)
                    );
                    App.MainWindow.AppWindow.TitleBar.SetDragRectangles(_dragRectTop);
                    break;
            }
        }

        private void CloseTitleBarButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is null) return;
            if (AppSettings.IsRunningBackend)
            {
                App.MainWindow.Hide();
            }
            else
            {
                _ = App.Current_Exit();
            }
        }

        private void NavigateTo(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
        {
            _pendingMusicDetails = null;
            _returnPlayingAfterDetails = false;
            _detailsPresenter.Dismiss();
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

        private void InitiaizeEqualizerDialog()
        {
            if (EqualizerDialog is null)
            {
                EqualizerDialog = new EqualizerDialog();
                EqualizerDialog.EqualizerCommitted += (s, e) =>
                {
                    // Fire-and-forget full state sync: idempotent, latest state wins;
                    // the dialog already debounces rapid slider drags before committing.
                    ViewModel.PlayerCommandService.EqUpdate();
                };
            }
        }

        private void SetSettingsDialog()
        {
            SettingsDialog ??= new SettingsDialog(ViewModel.AppViewModel);
        }

        private void NavigateToDefaultPage()
        {
            string entry = StartupEntry.Normalize(ViewModel.AppViewModel.DefaultEntryComboBoxTag);
            foreach (var item in NavigationViewControl.MenuItems)
            {
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == entry)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (entry)
            {
                case "Home":
                    NavigateTo(typeof(HomePage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "AddFolder":
                    NavigateTo(typeof(AddFolderPage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "MusicBrowse":
                    NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "PlayLists":
                    NavigateTo(typeof(PlayListPage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "Stats":
                    NavigateTo(typeof(StatsPage), null, new EntranceNavigationTransitionInfo());
                    break;
                default:
                    NavigateTo(typeof(HomePage), null, new EntranceNavigationTransitionInfo());
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                NavigationViewControl.Visibility = Visibility.Visible;
                _playingNavigation.Dismiss(300);
                ViewModel.AppViewModel.IsPlayingDetailVisible = false;
                ViewModel.AppViewModel.IsPointerOverTitleBar = true;
            }
            if (MainFrame.Content is not SettingsPage)
            {
                NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
                NavigateTo(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
            }
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            Type? targetType = null;

            if (args.IsSettingsInvoked)
            {
                targetType = typeof(SettingsPage);
            }
            else
            {
                targetType = args.InvokedItemContainer.Tag.ToString() switch
                {
                    "Home" => typeof(HomePage),
                    "AddFolder" => typeof(AddFolderPage),
                    "MusicBrowse" => typeof(MusicBrowsePage),
                    "PlayLists" => typeof(PlayListPage),
                    "Stats" => typeof(StatsPage),
                    _ => null
                };
            }

            if (targetType is not null && _detailsPresenter.IsOpen)
            {
                _detailsPresenter.Dismiss();
                _returnPlayingAfterDetails = false;
                ViewModel.AppViewModel.IsBackBtnEnable = _backBeforeMusicDetails;
            }
            if (targetType is not null && MainFrame.Content?.GetType() != targetType)
            {
                NavigateTo(targetType, null, new EntranceNavigationTransitionInfo());
            }
        }

        public void NavigateToMusicBrowsePage()
        {
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

        private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (MainFrame.Content is not HomePage || string.IsNullOrWhiteSpace(args.QueryText)) return;
            ViewModel.AppViewModel.State.Browse.SourceFilterId = -1;
            ViewModel.AppViewModel.SearchText = args.QueryText;
            NavigateToMusicBrowsePage();
            if (MainFrame.Content is MusicBrowsePage browse) browse.SelectBarItem("song");
        }

        public void NavigateToPlayingDetailPage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                    {
                        _isPageTransitioning = false;
                        OpenPendingDetails();
                    }
                }
                _playingNavigation.Show(typeof(PlayingDetailPage), 300, onCompleted: OnOneCompleted);
                NavigationViewControl.Visibility = Visibility.Collapsed;
                ViewModel.AppViewModel.IsPlayingDetailVisible = true;
                ViewModel.AppViewModel.IsPointerOverTitleBar = false;
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        public void NavigatebackToMusicBrowsePage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                    {
                        _isPageTransitioning = false;
                        OpenPendingDetails();
                    }
                }
                _playingNavigation.Dismiss(300, onCompleted: OnOneCompleted);
                NavigationViewControl.Visibility = Visibility.Visible;
                ViewModel.AppViewModel.IsPlayingDetailVisible = false;
                ViewModel.AppViewModel.IsPointerOverTitleBar = true;
            }
            else
            {
                _isPageTransitioning = false;
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

        private void QueueTitle_Click(object sender, RoutedEventArgs args)
        {
            if (sender is not Button { Tag: Music music } button) return;
            int index = ListViewInteraction.IndexFromSource(button, CurrentPlayListView);
            long entry = index >= 0 ? ViewModel.AppViewModel.State.Queue.EntryIdAt(index) : 0;
            ShowMusicDetails(new MusicDetailsRequest(music, null, entry));
        }

        public void HandleBackNavigation()
        {
            if (_detailsPresenter.IsOpen)
            {
                CloseMusicDetails();
                return;
            }
            if (MainFrame.Content is HomePage homePage && homePage.ViewModel.IsInDetailMode)
            {
                homePage.CollapseDetail();
                return;
            }
            if (MainFrame.Content is PlayListPage playListPage && playListPage.ViewModel.IsInDetailMode)
            {
                playListPage.CollapseDetail();
                return;
            }
            // 返回键/全局热键在任意主页面都会进入这里；只让当前曲库页处理，
            // 避免单例 ViewModel 保留的详情状态影响其他主页面。
            if (MainFrame.Content is MusicBrowsePage)
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().BackButton();
            }
        }

        private void NavigationViewControl_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            HandleBackNavigation();
        }

        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
            if (thumb is not null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
                //thumb.DragDelta += (s, e) =>
                //{
                //    _progressToolTip?.Content = ViewModel.AppViewModel.ProgressSliderThumbTipText;
                //};
                //ToolTipService.SetToolTip(thumb, _progressToolTip);
                //_progressToolTip.Opened += (s, e) =>
                //    _progressToolTip.Content = ViewModel.AppViewModel.ProgressSliderThumbTipText;
            }
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = false;
        }

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(VolumeButton).Properties.MouseWheelDelta;
            if (delta > 0)
            {
                ViewModel.AppViewModel.AdjustVolume(5);
            }
            else if (delta < 0)
            {
                ViewModel.AppViewModel.AdjustVolume(-5);
            }
            e.Handled = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = false;
            var (_, totalMs) = ViewModel.AppViewModel.GetTimeProgressCache();
            long newPosMs = Math.Max(0, Math.Min((long)(ViewModel.AppViewModel.ProgressSlider * 1000), totalMs));
            ViewModel.AppViewModel.IsManualSelect = true;
            try
            {
                ViewModel.PlayerCommandService.ChangeWaveChannelTime(newPosMs);
            }
            finally { ViewModel.AppViewModel.IsManualSelect = false; }
        }

        private void CurrentPlayListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ListViewInteraction.IsAction(e.OriginalSource as DependencyObject, CurrentPlayListView)) return;
            _ = App.Services.GetRequiredService<PlaybackCoordinator>().PlayAtAsync(CurrentPlayListView.SelectedIndex);
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

        private void CurrentPlayListButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = true;
        }

        private void CurrentPlayListTeachingTipCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = false;
        }

        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CurrentPlayListTeachingTip.IsOpen) return;
            int index = ViewModel.LocateCurrentTrack();
            if (index < 0 || index >= CurrentPlayListView.Items.Count) return;
            CurrentPlayListView.SelectedIndex = index;
            await _queueCentering.LocateIndexAsync(index);
        }

        private void CurrentPlayListTeachingTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
            => _queueCentering.Cancel();

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            NavigatebackToMusicBrowsePage();
        }
    }
}
