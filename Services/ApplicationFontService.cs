using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinUIMusicPlayer.Services;

/// <summary>应用字体的 XAML 资源与窗口根节点刷新；偏好及持久化仍由 SettingsCoordinator 管理。</summary>
public sealed class ApplicationFontService : IDisposable
{
    private static readonly string[] FontKeys =
        ["ContentControlThemeFontFamily", "PivotHeaderItemFontFamily", "PivotTitleFontFamily", "KeyTipFontFamily"];
    private readonly HashSet<FrameworkElement> _roots = [];
    private string? _appliedSource;
    private bool _disposed;

    public void Apply(FontFamily font)
    {
        if (_disposed || string.Equals(_appliedSource, font.Source, StringComparison.Ordinal)) return;
        foreach (string key in FontKeys) Application.Current.Resources[key] = font;
        _appliedSource = font.Source;
        // Theme callbacks may close or attach another window during refresh.
        var roots = new FrameworkElement[_roots.Count];
        _roots.CopyTo(roots);
        foreach (var root in roots)
        {
            if (_roots.Contains(root)) RefreshResources(root);
        }
    }

    internal void Register(FrameworkElement root)
    {
        if (!_disposed && _roots.Add(root)) RefreshResources(root);
    }

    internal void Unregister(FrameworkElement root) => _roots.Remove(root);

    private static void RefreshResources(FrameworkElement root)
    {
        // WinUIEx 的 WindowContent 会把 RequestedTheme 写回外层窗口根节点。
        // 在内层切换后恢复 Default 会覆盖窗口的显式明暗主题；因此只在实际 XAML 根节点
        // 刷新 ThemeResource，保留窗口主题以及内层的继承状态。
        FrameworkElement themeRoot = root.XamlRoot?.Content as FrameworkElement ?? root;
        ElementTheme requested = themeRoot.RequestedTheme;
        try
        {
            themeRoot.RequestedTheme = themeRoot.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        }
        finally
        {
            themeRoot.RequestedTheme = requested;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _roots.Clear();
    }
}
