using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper
{
    // 按 XamlRoot 缓存对话框，复用实例和输入控件；显示时同步程序主题与字体。
    internal static class DialogHelper
    {
        private static readonly ConditionalWeakTable<XamlRoot, ContentDialog> s_confirmCache = new();
        private static readonly ConditionalWeakTable<XamlRoot, ContentDialog> s_inputCache = new();
        private static readonly ConditionalWeakTable<XamlRoot, ContentDialog> s_artistPickerCache = new();
        private static string? s_artistPickerResult;

        public static async Task<bool> ShowConfirmAsync(XamlRoot xamlRoot, string titleKey)
        {
            var dialog = s_confirmCache.GetValue(xamlRoot, static root => new ContentDialog { XamlRoot = root });
            dialog.Title = ToolUtils.GetString(titleKey);
            dialog.Content = null;
            dialog.PrimaryButtonText = ToolUtils.GetString("PrimaryButton");
            dialog.CloseButtonText = ToolUtils.GetString("CloseButton");
            return await dialog.ShowThemedAsync(xamlRoot) == ContentDialogResult.Primary;
        }

        public static async Task<string> ShowInputAsync(XamlRoot xamlRoot, string titleKey, string prefillText)
        {
            var dialog = s_inputCache.GetValue(xamlRoot, static root => new ContentDialog { XamlRoot = root });
            dialog.Title = ToolUtils.GetString(titleKey);
            dialog.PrimaryButtonText = ToolUtils.GetString("PrimaryButton");
            dialog.CloseButtonText = ToolUtils.GetString("CloseButton");

            if (dialog.Content is not TextBox textBox)
            {
                textBox = new TextBox();
                dialog.Content = textBox;
            }
            textBox.Text = prefillText;

            if (await dialog.ShowThemedAsync(xamlRoot) == ContentDialogResult.Primary)
                return textBox.Text;

            return string.Empty;
        }

        // 缓存和新建对话框统一设置主题、XamlRoot 及模板字体，再交给 WinUI 显示。
        public static IAsyncOperation<ContentDialogResult> ShowThemedAsync(
            this ContentDialog dialog, XamlRoot xamlRoot)
        {
            dialog.RequestedTheme = AppSettings.ElementTheme;
            dialog.XamlRoot = xamlRoot;
            // 首次 ShowAsync 前模板可能尚未生成；Opened 补齐首次初始化。
            // 先移除再添加，重复显示同一实例不会累积订阅。处理器不捕获窗口或服务。
            dialog.Opened -= OnDialogOpened;
            dialog.Opened += OnDialogOpened;
            RefreshTemplateFont(dialog);
            return dialog.ShowAsync();
        }

        private static void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => RefreshTemplateFont(sender);

        private static void RefreshTemplateFont(ContentDialog dialog)
        {
            if (Application.Current.Resources["ContentControlThemeFontFamily"] is not FontFamily font
                || VisualTreeHelper.GetChildrenCount(dialog) == 0
                || VisualTreeHelper.GetChild(dialog, 0) is not FrameworkElement templateRoot) return;

            // WinUI 标准模板的这两个呈现器使用 StaticResource；窗口主题翻转不会
            // 更新缓存的字体。只修改模板字体边界，内容中的图标和显式字体仍可自行覆盖。
            if (templateRoot.FindName("Title") is ContentControl title) title.FontFamily = font;
            if (templateRoot.FindName("Content") is ContentPresenter content) content.FontFamily = font;
        }

        public static async Task<string?> ShowArtistPickerAsync(XamlRoot xamlRoot, IReadOnlyList<string> names)
        {
            var dialog = s_artistPickerCache.GetValue(xamlRoot, static root =>
            {
                var d = new ContentDialog { XamlRoot = root };
                var listView = new ListView
                {
                    SelectionMode = ListViewSelectionMode.Single,
                    IsItemClickEnabled = true,
                    MaxHeight = 360
                };
                listView.ItemClick += (_, e) =>
                {
                    s_artistPickerResult = e.ClickedItem as string;
                    d.Hide();
                };
                d.Content = listView;
                return d;
            });
            dialog.Title = ToolUtils.GetString("SelectArtist");
            dialog.PrimaryButtonText = null;
            dialog.CloseButtonText = ToolUtils.GetString("CloseButton");
            dialog.DefaultButton = ContentDialogButton.Close;
            ((ListView)dialog.Content).ItemsSource = names;

            s_artistPickerResult = null;
            await dialog.ShowThemedAsync(xamlRoot);
            return s_artistPickerResult;
        }
    }
}
