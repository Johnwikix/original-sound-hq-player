using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;

namespace WinUIMusicPlayer.Helper;

/// <summary>当前页面拥有的临时详情层。Frame、来源页面和列表始终保留，不创建返回导航。</summary>
public sealed class MusicDetailsPresenter : IDisposable
{
    private readonly Frame _frame;
    private readonly ConnectedAnimationScope _cover;
    private readonly ConnectedAnimationScope _title;
    private readonly UISettings _settings = new();
    private ContentControl? _source;
    private ContentControl? _host;
    private WeakReference<MusicListRowControl>? _row;
    private WeakReference<FrameworkElement>? _returnCover, _returnTitle;
    private Music? _music;
    private bool _closing;
    private bool _keyboardEntry;
    private int _revision;
    private Action? _closed;
    public MusicDetailsPanel? CurrentDetails => _host?.Content as MusicDetailsPanel;
    public bool IsOpen => CurrentDetails is not null;

    public MusicDetailsPresenter(Frame frame)
    {
        _frame = frame;
        _cover = new ConnectedAnimationScope(frame, "MusicDetailsCover");
        _title = new ConnectedAnimationScope(frame, "MusicDetailsTitle");
        frame.Navigating += FrameNavigating;
        frame.Unloaded += FrameUnloaded;
    }
    public bool Show(MusicDetailsRequest request, FrameworkElement? sourceCover = null, FrameworkElement? sourceTitle = null)
    {
        if (!_closing && CurrentDetails is { } current && ReferenceEquals(current.Music, request.Music))
        {
            current.UpdatePlaybackContext(request);
            return true;
        }
        if (_closing) Dismiss();
        if (_frame.Content is not Page page
            || page.FindName("MusicDetailsSource") is not ContentControl source
            || page.FindName("MusicDetailsOverlay") is not ContentControl host) return false;
        Cancel();
        _source = source;
        _host = host;
        _music = request.Music;
        _returnCover = sourceCover is null ? null : new(sourceCover);
        _returnTitle = sourceTitle is null ? null : new(sourceTitle);
        var row = AncestorRow(sourceCover ?? sourceTitle);
        _row = row is null ? null : new(row);
        _keyboardEntry = row is not null && _frame.XamlRoot is { } xamlRoot
            && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) is Control { FocusState: FocusState.Keyboard } focused
            && ReferenceEquals(AncestorRow(focused), row);
        if (_settings.AnimationsEnabled)
        {
            _cover.Prepare(sourceCover, new GravityConnectedAnimationConfiguration());
            _title.Prepare(sourceTitle, new GravityConnectedAnimationConfiguration());
        }
        row?.StopAutoScroll();
        CurrentDetails?.Release();
        var details = new MusicDetailsPanel();
        if (!details.Open(request)) { Dismiss(); return false; }
        host.Content = details;
        host.Opacity = 1;
        host.Visibility = Visibility.Visible;
        host.IsHitTestVisible = true;
        source.Opacity = 0;
        source.IsEnabled = false;
        if (page is HomePage home) home.SetExternalDetailsVisible(true);
        _cover.StartDeferred(() => CurrentDetails?.CoverElement);
        _title.StartDeferred(() => CurrentDetails?.TitleElement);
        _frame.DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(CurrentDetails, details) && !_closing) details.FocusHeader(); });
        return true;
    }
    public bool Back(Action? closed = null)
    {
        if (CurrentDetails is not { } details || _source is null || _host is null || _closing) return false;
        Cancel();
        _closing = true;
        _closed = closed;
        int revision = _revision;
        if (_settings.AnimationsEnabled)
        {
            _cover.Prepare(details.CoverElement, new DirectConnectedAnimationConfiguration());
            _title.Prepare(details.TitleElement, new DirectConnectedAnimationConfiguration());
        }
        details.BeginClose();
        _source.Opacity = 1;
        _source.IsEnabled = true;
        _host.Opacity = 0;
        _host.IsHitTestVisible = false;
        int remaining = 2;
        void Completed()
        {
            if (revision == _revision && --remaining == 0) FinishBack();
        }
        _cover.StartDeferred(ReturnCover, Completed);
        _title.StartDeferred(ReturnTitle, Completed);
        return true;
    }
    private void FinishBack()
    {
        var focus = ReturnTitle();
        var callback = _closed;
        bool keyboardEntry = _keyboardEntry;
        _closed = null;
        // 验证按钮完全在视口内，避免 Focus 引发一次隐式 BringIntoView。
        Button? button = null;
        if (keyboardEntry)
            for (var element = focus; element is not null; element = VisualTreeHelper.GetParent(element) as FrameworkElement)
                if (element is Button candidate) { button = candidate; break; }
        bool focusVisible = button is not null && IsFullyVisible(button);
        Dismiss();
        if (focusVisible) button!.Focus(FocusState.Keyboard);
        callback?.Invoke();
    }
    private FrameworkElement? ReturnCover() => ReturnRow()?.CoverElement ?? ValidOriginal(_returnCover);
    private FrameworkElement? ReturnTitle() => ReturnRow()?.TitleElement ?? ValidOriginal(_returnTitle);
    private MusicListRowControl? ReturnRow()
    {
        if (_row?.TryGetTarget(out var row) == true && Matches(row.Music) && IsVisible(row)) return row;
        return _source is null ? null : FindRow(_source);
    }
    private MusicListRowControl? FindRow(DependencyObject parent)
    {
        if (parent is MusicListRowControl row && Matches(row.Music) && IsVisible(row)) return row;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindRow(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private bool Matches(Music? music) => music is not null && _music is not null
        && (ReferenceEquals(music, _music) || CurrentTrackLookup.Matches(music, _music));
    private FrameworkElement? ValidOriginal(WeakReference<FrameworkElement>? reference)
    {
        if (reference?.TryGetTarget(out var element) != true || !IsVisible(element)) return null;
        if (AncestorRow(element) is { } row && !Matches(row.Music)) return null;
        return element;
    }
    private bool IsVisible(FrameworkElement element)
    {
        if (_source is null || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0
            || element.XamlRoot != _source.XamlRoot) return false;
        DependencyObject? parent = element;
        while (parent is not null && !ReferenceEquals(parent, _source))
        {
            if (parent is ScrollViewer or ScrollView)
            {
                var viewport = (FrameworkElement)parent;
                var rect = element.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (rect.Right <= 0 || rect.Left >= viewport.ActualWidth || rect.Bottom <= 0 || rect.Top >= viewport.ActualHeight) return false;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return parent is not null;
    }
    private bool IsFullyVisible(FrameworkElement element)
    {
        for (DependencyObject? parent = element; parent is not null && !ReferenceEquals(parent, _source); parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer or ScrollView)
            {
                var viewport = (FrameworkElement)parent;
                var rect = element.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (rect.Left < 0 || rect.Top < 0 || rect.Right > viewport.ActualWidth || rect.Bottom > viewport.ActualHeight) return false;
            }
        return true;
    }
    private static MusicListRowControl? AncestorRow(FrameworkElement? element)
    {
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is MusicListRowControl row) return row;
        return null;
    }
    public void Cancel()
    {
        _revision++;
        _closed = null;
        _cover.Cancel();
        _title.Cancel();
    }
    public void Dismiss()
    {
        Cancel();
        CurrentDetails?.Release();
        if (_host is { } host)
        {
            host.Content = null;
            host.Visibility = Visibility.Collapsed;
            host.Opacity = 1;
            host.IsHitTestVisible = true;
        }
        if (_source is { } source)
        {
            source.Opacity = 1;
            source.IsEnabled = true;
        }
        if (_frame.Content is HomePage home) home.SetExternalDetailsVisible(false);
        _host = null;
        _source = null;
        _row = null;
        _returnCover = null;
        _returnTitle = null;
        _music = null;
        _closing = false;
        _keyboardEntry = false;
    }
    private void FrameNavigating(object sender, NavigatingCancelEventArgs args) => Dismiss();
    private void FrameUnloaded(object sender, RoutedEventArgs args) => Dismiss();
    public void Dispose()
    {
        Dismiss();
        _frame.Navigating -= FrameNavigating;
        _frame.Unloaded -= FrameUnloaded;
        _cover.Dispose();
        _title.Dispose();
    }
}
