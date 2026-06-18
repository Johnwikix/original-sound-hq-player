using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper
{
    // 零分配 ContentDialog 辅助：ConditionalWeakTable 按 XamlRoot 缓存实例，
    // 避免每次 new ContentDialog 的堆分配；static lambda 无闭包捕获
    internal static class DialogHelper
    {
        private static readonly ConditionalWeakTable<XamlRoot, ContentDialog> s_confirmCache = new();
        private static readonly ConditionalWeakTable<XamlRoot, ContentDialog> s_inputCache = new();

        public static async Task<bool> ShowConfirmAsync(XamlRoot xamlRoot, string titleKey)
        {
            var dialog = s_confirmCache.GetValue(xamlRoot, static root => new ContentDialog { XamlRoot = root });
            dialog.Title = ToolUtils.GetString(titleKey);
            dialog.Content = null;
            dialog.PrimaryButtonText = ToolUtils.GetString("PrimaryButton");
            dialog.CloseButtonText = ToolUtils.GetString("CloseButton");
            dialog.RequestedTheme = AppSettings.ElementTheme;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        public static async Task<string> ShowInputAsync(XamlRoot xamlRoot, string titleKey, string prefillText)
        {
            var dialog = s_inputCache.GetValue(xamlRoot, static root => new ContentDialog { XamlRoot = root });
            dialog.Title = ToolUtils.GetString(titleKey);
            dialog.PrimaryButtonText = ToolUtils.GetString("PrimaryButton");
            dialog.CloseButtonText = ToolUtils.GetString("CloseButton");
            dialog.RequestedTheme = AppSettings.ElementTheme;

            if (dialog.Content is not TextBox textBox)
            {
                textBox = new TextBox();
                dialog.Content = textBox;
            }
            textBox.Text = prefillText;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                return textBox.Text;

            return string.Empty;
        }

        // 单例 Dialog（Equalizer/Settings/Progress）统一 show 样板：
        // 设置主题 + XamlRoot 后直接 ShowAsync，零分配（静态扩展方法，无闭包）
        public static IAsyncOperation<ContentDialogResult> ShowThemedAsync(
            this ContentDialog dialog, XamlRoot xamlRoot)
        {
            dialog.RequestedTheme = AppSettings.ElementTheme;
            dialog.XamlRoot = xamlRoot;
            return dialog.ShowAsync();
        }
    }
}
