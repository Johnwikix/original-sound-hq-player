using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>以普通卡片外观提供歌曲详情的点击、键盘与辅助技术入口。</summary>
public sealed partial class HomeSongItem : ContentControl
{
    /// <summary>在用户请求打开歌曲详情时通知宿主，不触发播放。</summary>
    public event EventHandler? Invoked;

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs args)
    {
        base.OnTapped(args);
        if (args.Handled) return;
        Focus(FocusState.Pointer);
        Invoke();
        args.Handled = true;
    }
    /// <inheritdoc/>
    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Handled || args.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        Invoke();
        args.Handled = true;
    }
    internal void Invoke()
    {
        if (IsLoaded && IsEnabled && Visibility == Visibility.Visible) Invoked?.Invoke(this, EventArgs.Empty);
    }
    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new SongItemAutomationPeer(this);

    private sealed partial class SongItemAutomationPeer(HomeSongItem owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(HomeSongItem);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);
        // UIA 调用转回控件 Dispatcher；输入路径不创建曲目或队列副本。
        public void Invoke() => owner.DispatcherQueue.TryEnqueue(owner.Invoke);
    }
}
