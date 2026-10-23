using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Utils;

/// <summary>仅在每个窗口根节点启用；加载/卸载时登记字体资源刷新边界。</summary>
public static class ApplicationFont
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ApplicationFont), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty ServiceProperty = DependencyProperty.RegisterAttached(
        "Service", typeof(object), typeof(ApplicationFont), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement root) return;
        if ((bool)args.NewValue)
        {
            // 仅在挂接时解析容器；窗口卸载可能晚于 Host.Dispose，收尾必须使用已捕获服务。
            root.SetValue(ServiceProperty, App.Services.GetRequiredService<ApplicationFontService>());
            root.Loaded += OnLoaded;
            root.Unloaded += OnUnloaded;
            // 顶层窗口内容可已连接 XamlRoot，但 IsLoaded 仍为 false。
            if (root.XamlRoot is not null) Register(root);
        }
        else
        {
            root.Loaded -= OnLoaded;
            root.Unloaded -= OnUnloaded;
            GetService(root)?.Unregister(root);
            root.ClearValue(ServiceProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Register((FrameworkElement)sender);
    private static ApplicationFontService? GetService(FrameworkElement root) => (ApplicationFontService?)root.GetValue(ServiceProperty);
    private static void Register(FrameworkElement root) => GetService(root)?.Register(root);
    private static void OnUnloaded(object sender, RoutedEventArgs args) => GetService((FrameworkElement)sender)?.Unregister((FrameworkElement)sender);
}
