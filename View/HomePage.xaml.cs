using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool _pendingKeyboardPreview;
    private bool _keyboardPreview;
    private bool _collapsingPreview;
    private bool _restoringFocus;
    private bool _sourcePointerInside;
    private bool _previewPointerInside;
    /// <summary>获取稳定的首页 ViewModel。</summary>
    public HomeViewModel ViewModel { get; }
    /// <summary>初始化页面，不阻塞主窗口启动。</summary>
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        _openPreviewTimer = DispatcherQueue.CreateTimer();
        _openPreviewTimer.IsRepeating = false;
        _openPreviewTimer.Tick += OpenPreview_Tick;
        _closePreviewTimer = DispatcherQueue.CreateTimer();
        _closePreviewTimer.IsRepeating = false;
        _closePreviewTimer.Tick += ClosePreview_Tick;
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
        Bindings.Initialize();
        Bindings.Update();
        ViewModel.Activate(DispatcherQueue);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        DismissPreview();
        ViewModel.Deactivate();
        DropOverlay.Visibility = Visibility.Collapsed;
        Bindings.StopTracking();
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HomeViewModel.PreviewSong) && ViewModel.PreviewSong is null) ResetPreview();
    }
    private void Song_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch && sender is FrameworkElement source)
            SchedulePreview(source, false);
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
    private void Song_GotFocus(object sender, RoutedEventArgs args)
    {
        if (!_restoringFocus && sender is FrameworkElement source && args.OriginalSource is Control { FocusState: FocusState.Keyboard })
            SchedulePreview(source, true);
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
    private void SchedulePreview(FrameworkElement source, bool keyboard)
    {
        if (SongPreview.IsPlaylistMenuOpen) return;
        _closePreviewTimer.Stop();
        if (ReferenceEquals(source, _previewSource) && ViewModel.PreviewSong is not null)
        {
            _pendingPreviewSource = null;
            _sourcePointerInside |= !keyboard;
            SongPreview.Restore();
            _collapsingPreview = false;
            return;
        }
        if (ViewModel.PreviewSong is not null)
        {
            // 移向相邻卡片时先完成旧卡片的退出，不能直接清空曲目导致动画被截断。
            _pendingPreviewSource = source;
            _pendingKeyboardPreview = keyboard;
            BeginPreviewClose();
            return;
        }
        DismissPreview();
        _previewSource = source;
        _keyboardPreview = keyboard;
        _sourcePointerInside = !keyboard;
        _openPreviewTimer.Interval = TimeSpan.FromMilliseconds(keyboard ? 180 : 400);
        _openPreviewTimer.Start();
    }
    private void OpenPreview_Tick(DispatcherQueueTimer sender, object args) => OpenPreview();
    private void OpenPreview()
    {
        if (!IsLoaded || _previewSource is not { IsLoaded: true, Tag: HomeSong song } source
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
        if (_keyboardPreview) SongPreview.FocusActions();
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
        if (!IsLoaded || SongPreview.IsPlaylistMenuOpen || HasKeyboardFocus() || _sourcePointerInside || _previewPointerInside || _closePreviewTimer.IsRunning) return;
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
        bool keyboard = _pendingKeyboardPreview;
        DismissPreview();
        if (pending is { IsLoaded: true }) SchedulePreview(pending, keyboard);
    }
    private void Preview_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        var source = _previewSource;
        _restoringFocus = true;
        if (source is Control control) control.Focus(FocusState.Keyboard);
        _pendingPreviewSource = null;
        BeginPreviewClose();
        _restoringFocus = false;
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
        bool restoring = _restoringFocus;
        _restoringFocus = true;
        // 滚动收起时把焦点交给视口，避免原卡片的 BringIntoView 把页面拉回旧位置。
        if (XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SongPreview))
            ContentScroll.Focus(FocusState.Programmatic);
        SongPreview.DismissMenus();
        SongPreview.Restore();
        SongPreview.Visibility = Visibility.Collapsed;
        _restoringFocus = restoring;
    }
    private void DismissPreview()
    {
        ViewModel.DismissPreview();
        ResetPreview();
    }
    private void Content_ViewChanging(object sender, ScrollViewerViewChangingEventArgs args) => HandleScroll();
    private void Shelf_Paging(object? sender, EventArgs args) => HandleScroll();
    private void HandleScroll()
    {
        // 键盘聚焦会先把原卡片滚入视口，等原生 BringIntoView 稳定后再测量展开起点。
        if (_keyboardPreview && _previewSource is not null && ViewModel.PreviewSong is null && HasKeyboardFocus())
        {
            _openPreviewTimer.Stop();
            _openPreviewTimer.Interval = TimeSpan.FromMilliseconds(180);
            _openPreviewTimer.Start();
            return;
        }
        DismissPreview();
    }
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
