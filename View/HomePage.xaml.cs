using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.View.Controls;

namespace WinUIMusicPlayer.View;

/// <summary>显示首页并转发拖放交互，状态和播放均由 ViewModel 管理。</summary>
public sealed partial class HomePage : Page
{
    private readonly DispatcherQueueTimer _openPreviewTimer;
    private readonly DispatcherQueueTimer _closePreviewTimer;
    private FrameworkElement? _previewSource;
    private FrameworkElement? _pendingPreviewSource;
    private bool _collapsingPreview;
    private bool _previewHandoff;
    private bool _sourcePointerInside;
    private bool _previewPointerInside;
    private readonly UISettings _settings = new();
    private ConnectedAnimation? _detailAnimation;
    private bool _detailTransitionPending;
    private bool _detailTransitioning;
    private WeakReference<FrameworkElement>? _detailSource;
    private int _detailMusicId;
    private bool _detailKeyboardEntry;
    private bool _resumeShuffle;
    private bool _shuffleSuspended;
    private MusicDetailsEditorViewModel? _observedEditor;
    /// <summary>获取稳定的首页 ViewModel。</summary>
    public HomeViewModel ViewModel { get; }
    /// <summary>初始化页面，不阻塞主窗口启动。</summary>
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
        _openPreviewTimer = DispatcherQueue.CreateTimer();
        _openPreviewTimer.IsRepeating = false;
        _closePreviewTimer = DispatcherQueue.CreateTimer();
        _closePreviewTimer.IsRepeating = false;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    /// <summary>转换非空集合的可见性。</summary>
    public Visibility ShowCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>转换非空消息状态。</summary>
    public bool HasText(string text) => !string.IsNullOrWhiteSpace(text);
    /// <summary>将通用轮播选项适配为首页主题，主视图保持同一个实例。</summary>
    public HomeTheme? SelectedTheme(int index, int count) => index >= 0 && index < count ? Featured.SelectedItem as HomeTheme : null;
    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _openPreviewTimer.Tick -= OpenPreview_Tick;
        _closePreviewTimer.Tick -= ClosePreview_Tick;
        _openPreviewTimer.Tick += OpenPreview_Tick;
        _closePreviewTimer.Tick += ClosePreview_Tick;
        Bindings.Initialize();
        Bindings.Update();
        ViewModel.Activate(DispatcherQueue);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ObserveEditor();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        // Stop 只停止调度；解绑 WinRT 定时器回调，避免队列通过委托继续保留页面。
        _openPreviewTimer.Tick -= OpenPreview_Tick;
        _closePreviewTimer.Tick -= ClosePreview_Tick;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_observedEditor is not null) _observedEditor.Saved -= Editor_Saved;
        _observedEditor = null;
        CancelDetailTransition();
        DetailView.DismissMenus();
        _detailSource = null;
        RestoreShuffle();
        DismissPreview();
        ViewModel.Deactivate();
        DropOverlay.Visibility = Visibility.Collapsed;
        Bindings.StopTracking();
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HomeViewModel.DetailEditor)) ObserveEditor();
        if (args.PropertyName == nameof(HomeViewModel.PreviewSong) && ViewModel.PreviewSong is null) ResetPreview();
        if (args.PropertyName == nameof(HomeViewModel.DetailSong) && ViewModel.DetailSong is null)
        {
            CancelDetailTransition();
            DetailView.DismissMenus();
            _detailSource = null;
            RestoreShuffle();
        }
    }
    private void ObserveEditor()
    {
        if (_observedEditor is not null) _observedEditor.Saved -= Editor_Saved;
        _observedEditor = ViewModel.DetailEditor;
        if (_observedEditor is not null) _observedEditor.Saved += Editor_Saved;
    }
    private void Editor_Saved(object? sender, EventArgs args)
    {
        if (IsLoaded && ReferenceEquals(sender, ViewModel.DetailEditor)) CollapseDetail();
    }
    private void Song_Invoked(object? sender, EventArgs args)
    {
        if (sender is FrameworkElement { Tag: HomeSong song } source) OpenSongDetails(source, song, FindArtwork(source), source is Control { FocusState: FocusState.Keyboard });
    }
    private void Preview_Tapped(object sender, TappedRoutedEventArgs args)
    {
        for (var element = args.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, SongPreview); element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        SongPreview.Invoke();
        args.Handled = true;
    }
    private void Preview_Invoked(object? sender, EventArgs args)
    {
        if (_previewSource is { } source && SongPreview.Song is { } song)
            OpenSongDetails(source, song, FindArtwork(SongPreview), false);
    }
    private void OpenSongDetails(FrameworkElement source, HomeSong song, FrameworkElement? artwork, bool keyboard)
    {
        if (!IsLoaded || _detailTransitioning || ViewModel.HasDetails) return;
        var animation = PrepareDetailAnimation(artwork, false);
        _previewHandoff = animation is not null && IsWithin(artwork, SongPreview) && ViewModel.PreviewSong is not null;
        if (_previewHandoff)
        {
            _openPreviewTimer.Stop();
            _closePreviewTimer.Stop();
            _pendingPreviewSource = null;
        }
        if (!ViewModel.OpenDetails(song, preservePreview: _previewHandoff))
        {
            _previewHandoff = false;
            animation?.Cancel();
            return;
        }
        _detailSource = new WeakReference<FrameworkElement>(source);
        _detailMusicId = song.Music.Id;
        _detailKeyboardEntry = keyboard;
        _resumeShuffle = Featured.AutoShuffle;
        _shuffleSuspended = true;
        Featured.AutoShuffle = false;
        _detailAnimation = animation;
        QueueDetailTransition();
        DetailView.FocusHeader();
    }
    private ConnectedAnimation? PrepareDetailAnimation(FrameworkElement? source, bool returning)
    {
        if (!_settings.AnimationsEnabled || source is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }
            || source.XamlRoot != XamlRoot || XamlRoot is null) return null;
        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("HomeSongCover", source);
            if (returning) animation.Configuration = new DirectConnectedAnimationConfiguration();
            return animation;
        }
        catch (COMException ex)
        {
            App.GetLogger<HomePage>().LogDebug(ex, "首页封面已失效，跳过共享动画");
            return null;
        }
    }
    private void QueueDetailTransition()
    {
        _detailTransitioning = true;
        _detailTransitionPending = true;
        if (!DispatcherQueue.TryEnqueue(StartDetailTransition)) FinishDetailTransition();
    }
    private void StartDetailTransition()
    {
        if (!_detailTransitionPending) return;
        _detailTransitionPending = false;
        if (!IsLoaded || !ViewModel.HasDetails) { CancelDetailTransition(); return; }
        UpdateLayout();
        if (ViewModel.IsInDetailMode) DetailView.FocusHeader();
        var target = ViewModel.IsInDetailMode ? DetailView.CoverElement : ReturnArtwork();
        if (_detailAnimation is not { } animation || target is null)
        {
            FinishDetailTransition();
            return;
        }
        animation.Completed += DetailAnimation_Completed;
        try
        {
            bool started = ViewModel.IsInDetailMode ? animation.TryStart(target, DetailView.CoordinatedElements) : animation.TryStart(target);
            if (!started) FinishDetailTransition();
        }
        catch (COMException ex)
        {
            App.GetLogger<HomePage>().LogDebug(ex, "首页动画目的元素已失效，完成无动画切换");
            FinishDetailTransition();
        }
    }
    private void DetailAnimation_Completed(ConnectedAnimation sender, object args)
    {
        if (ReferenceEquals(sender, _detailAnimation)) FinishDetailTransition();
    }
    private FrameworkElement? ReturnSource()
    {
        if (_detailSource is null || !_detailSource.TryGetTarget(out var source) || !source.IsLoaded
            || source.Tag is not HomeSong song || song.Music.Id != _detailMusicId || source.XamlRoot != XamlRoot) return null;
        return source;
    }
    private FrameworkElement? ReturnArtwork()
    {
        if (ReturnSource() is not { } source || FindArtwork(source) is not { } artwork) return null;
        var point = artwork.TransformToVisual(ContentScroll).TransformPoint(new Point());
        return point.X + artwork.ActualWidth > 0 && point.X < ContentScroll.ViewportWidth
            && point.Y + artwork.ActualHeight > 0 && point.Y < ContentScroll.ViewportHeight ? artwork : null;
    }
    private void FinishDetailTransition()
    {
        var source = IsLoaded && ReturnArtwork() is not null ? ReturnSource() : null;
        CancelDetailTransition();
        if (ViewModel.IsInDetailMode) return;
        ViewModel.CompleteCloseDetails();
        if (IsLoaded)
        {
            if (_detailKeyboardEntry && source is Control control) control.Focus(FocusState.Keyboard);
            else RefreshButton.Focus(FocusState.Programmatic);
        }
    }
    private void RestoreShuffle()
    {
        if (!_shuffleSuspended) return;
        _shuffleSuspended = false;
        Featured.AutoShuffle = _resumeShuffle;
    }
    private void CancelDetailTransition()
    {
        _detailTransitionPending = false;
        _detailTransitioning = false;
        if (_previewHandoff)
        {
            _previewHandoff = false;
            DismissPreview();
        }
        if (_detailAnimation is not { } animation) return;
        _detailAnimation = null;
        animation.Completed -= DetailAnimation_Completed;
        animation.Cancel();
    }
    /// <summary>返回首页，并在原卡片仍有效时执行反向封面动画。</summary>
    public void CollapseDetail()
    {
        if (!ViewModel.IsInDetailMode) return;
        CancelDetailTransition();
        DetailView.DismissMenus();
        _detailAnimation = IsLoaded && ReturnArtwork() is not null ? PrepareDetailAnimation(DetailView.CoverElement, true) : null;
        ViewModel.BeginCloseDetails();
        if (IsLoaded) QueueDetailTransition();
        else FinishDetailTransition();
    }
    private void Home_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || !ViewModel.IsInDetailMode) return;
        CollapseDetail();
        args.Handled = true;
    }
    private void Song_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch && sender is FrameworkElement source)
            SchedulePreview(source);
    }
    private void Song_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (ReferenceEquals(sender, _pendingPreviewSource)) _pendingPreviewSource = null;
        if (ReferenceEquals(sender, _previewSource))
        {
            _sourcePointerInside = false;
            ScheduleClose();
        }
    }
    private void Song_LostFocus(object sender, RoutedEventArgs args) => DispatcherQueue.TryEnqueue(CheckPreviewFocus);
    private void CheckPreviewFocus()
    {
        if (!IsLoaded) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (!IsWithin(focused, SongPreview) && !IsWithin(focused, _previewSource)) ScheduleClose();
    }
    private static bool IsWithin(DependencyObject? child, DependencyObject? parent)
    {
        if (parent is null) return false;
        while (child is not null)
        {
            if (ReferenceEquals(child, parent)) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }
    private bool HasKeyboardFocus() => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } focused
        && (IsWithin(focused, SongPreview) || IsWithin(focused, _previewSource));
    private void SchedulePreview(FrameworkElement source)
    {
        if (ViewModel.HasDetails || _detailTransitioning || SongPreview.IsPlaylistMenuOpen) return;
        _closePreviewTimer.Stop();
        if (ReferenceEquals(source, _previewSource) && ViewModel.PreviewSong is not null)
        {
            _pendingPreviewSource = null;
            _sourcePointerInside = true;
            SongPreview.Restore();
            _collapsingPreview = false;
            return;
        }
        if (ViewModel.PreviewSong is not null)
        {
            // 移向相邻卡片时先完成旧卡片的退出，不能直接清空曲目导致动画被截断。
            _pendingPreviewSource = source;
            BeginPreviewClose();
            return;
        }
        DismissPreview();
        _previewSource = source;
        _sourcePointerInside = true;
        _openPreviewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _openPreviewTimer.Start();
    }
    private void OpenPreview_Tick(DispatcherQueueTimer sender, object args) => OpenPreview();
    private void OpenPreview()
    {
        if (!IsLoaded || ViewModel.HasDetails || _previewSource is not { IsLoaded: true, Tag: HomeSong song } source
            || ContentScroll.ViewportWidth < 220 || ContentScroll.ViewportHeight < 420) return;
        var artwork = FindArtwork(source);
        if (artwork is null || artwork.ActualWidth <= 0 || artwork.ActualHeight <= 0) return;
        var origin = artwork.TransformToVisual(PreviewLayer).TransformPoint(new Point());
        var sourceSize = new Size(artwork.ActualWidth, artwork.ActualHeight);
        double visibleTop = ContentScroll.VerticalOffset;
        double visibleBottom = visibleTop + ContentScroll.ViewportHeight;
        if (origin.Y + sourceSize.Height < visibleTop || origin.Y > visibleBottom) return;
        ViewModel.ShowPreview(song);
        if (!ReferenceEquals(ViewModel.PreviewSong, song)) return;
        SongPreview.Fit(new Size(ContentScroll.ViewportWidth, ContentScroll.ViewportHeight), sourceSize);
        SongPreview.Visibility = Visibility.Visible;
        SongPreview.UpdateLayout();
        var destination = new Point(
            Math.Clamp(origin.X + (sourceSize.Width - SongPreview.ActualWidth) / 2, 12, Math.Max(12, ContentScroll.ViewportWidth - SongPreview.ActualWidth - 12)),
            Math.Clamp(origin.Y - 20, visibleTop + 12, Math.Max(visibleTop + 12, visibleBottom - SongPreview.ActualHeight - 12)));
        Canvas.SetLeft(SongPreview, destination.X);
        Canvas.SetTop(SongPreview, destination.Y);
        SongPreview.Reveal(new Rect(origin, sourceSize), destination);
    }
    private static MusicCoverControl? FindArtwork(DependencyObject parent)
    {
        if (parent is MusicCoverControl artwork) return artwork;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindArtwork(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private void Preview_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        // 浮层已经限制在可见视口内，按钮聚焦不应再按展开动画中的几何位置滚动页面。
        args.Handled = true;
    }
    private void Preview_MenuStateChanged(object? sender, EventArgs args)
    {
        if (!IsLoaded || ViewModel.PreviewSong is null) return;
        if (SongPreview.IsPlaylistMenuOpen)
        {
            _closePreviewTimer.Stop();
            if (_collapsingPreview) SongPreview.Restore();
            _collapsingPreview = false;
        }
        else ScheduleClose();
    }
    private void Preview_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pendingPreviewSource = null;
        _previewPointerInside = true;
        _closePreviewTimer.Stop();
        if (_collapsingPreview) SongPreview.Restore();
        _collapsingPreview = false;
    }
    private void Preview_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _previewPointerInside = false;
        ScheduleClose();
    }
    private void ScheduleClose()
    {
        if (_previewHandoff || !IsLoaded || SongPreview.IsPlaylistMenuOpen || HasKeyboardFocus() || _sourcePointerInside || _previewPointerInside || _closePreviewTimer.IsRunning) return;
        _openPreviewTimer.Stop();
        _closePreviewTimer.Interval = TimeSpan.FromMilliseconds(160);
        _closePreviewTimer.Start();
    }
    private void ClosePreview_Tick(DispatcherQueueTimer sender, object args)
    {
        if (ViewModel.PreviewSong is null) { DismissPreview(); return; }
        BeginPreviewClose();
    }
    private void BeginPreviewClose()
    {
        if (_previewHandoff) return;
        _closePreviewTimer.Stop();
        _openPreviewTimer.Stop();
        if (_collapsingPreview) return;
        _collapsingPreview = true;
        SongPreview.CollapseToCover();
    }
    private void Preview_CloseAnimationCompleted(object? sender, EventArgs args)
    {
        if (!_collapsingPreview) return;
        var pending = _pendingPreviewSource;
        DismissPreview();
        if (pending is { IsLoaded: true }) SchedulePreview(pending);
    }
    private void Preview_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        var source = _previewSource;
        if (source is Control control) control.Focus(FocusState.Keyboard);
        _pendingPreviewSource = null;
        BeginPreviewClose();
        args.Handled = true;
    }
    private void ResetPreview()
    {
        _openPreviewTimer.Stop();
        _closePreviewTimer.Stop();
        _previewSource = null;
        _pendingPreviewSource = null;
        _collapsingPreview = false;
        _sourcePointerInside = false;
        _previewPointerInside = false;
        // 滚动收起时把焦点交给视口，避免原卡片的 BringIntoView 把页面拉回旧位置。
        if (XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SongPreview))
            ContentScroll.Focus(FocusState.Programmatic);
        SongPreview.DismissMenus();
        SongPreview.Restore();
        SongPreview.Visibility = Visibility.Collapsed;
    }
    private void DismissPreview()
    {
        ViewModel.DismissPreview();
        ResetPreview();
    }
    private void Content_ViewChanging(object sender, ScrollViewerViewChangingEventArgs args) => HandleScroll();
    private void Shelf_Paging(object? sender, EventArgs args) => HandleScroll();
    private void HandleScroll() => DismissPreview();
    private void PreviewLayer_SizeChanged(object sender, SizeChangedEventArgs args) => DismissPreview();
    private void Empty_DragOver(object sender, DragEventArgs args)
    {
        bool accept = !ViewModel.IsScanning && args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = accept ? DataPackageOperation.Link : DataPackageOperation.None;
        DropOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Empty_DragLeave(object sender, DragEventArgs args) => DropOverlay.Visibility = Visibility.Collapsed;
    private async void Empty_Drop(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (ViewModel.IsScanning || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        try
        {
            var folders = new List<IStorageItem>();
            foreach (var item in await args.DataView.GetStorageItemsAsync())
                if (item.IsOfType(StorageItemTypes.Folder)) folders.Add(item);
            if (IsLoaded && folders.Count > 0) await ViewModel.DropFoldersAsync(folders);
        }
        catch (Exception ex) { App.GetLogger<HomePage>().LogError(ex, "首页拖入文件夹失败"); }
        finally { deferral.Complete(); }
    }
}
