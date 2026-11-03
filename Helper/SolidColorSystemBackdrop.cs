using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.UI;
using CompositionColorBrush = Windows.UI.Composition.CompositionColorBrush;

namespace WinUIMusicPlayer.Helper;

/// <summary>在系统不支持云母时提供跟随窗口主题的不透明底色。</summary>
internal sealed partial class SolidColorSystemBackdrop : WinUIEx.CompositionBrushBackdrop
{
    private FrameworkElement? _rootElement;
    private CompositionColorBrush? _brush;

    /// <inheritdoc/>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        _rootElement = xamlRoot.Content as FrameworkElement;
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_rootElement is not null)
        {
            _rootElement.ActualThemeChanged += RootElement_ActualThemeChanged;
        }
    }

    /// <inheritdoc/>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (_rootElement is not null)
        {
            _rootElement.ActualThemeChanged -= RootElement_ActualThemeChanged;
            _rootElement = null;
        }
        _brush = null;
        // 基类负责从目标移除并释放合成画刷。
        base.OnTargetDisconnected(disconnectedTarget);
    }

    /// <inheritdoc/>
    protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
    {
        _brush = compositor.CreateColorBrush();
        UpdateColor();
        return _brush;
    }

    private void RootElement_ActualThemeChanged(FrameworkElement sender, object args) => UpdateColor();

    private void UpdateColor()
    {
        if (_brush is null) return;

        ElementTheme theme = _rootElement?.ActualTheme ?? ElementTheme.Default;
        bool isDark = theme == ElementTheme.Dark
            || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        // 主题切换只更新既有画刷；回退底色始终完全不透明。
        _brush.Color = isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 255, 255, 255);
    }
}
