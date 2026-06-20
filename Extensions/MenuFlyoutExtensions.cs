using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Extensions
{
    public static class MenuFlyoutExtensions
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.RegisterAttached("ItemsSource", typeof(IEnumerable<MenuModel>),
            typeof(MenuFlyoutExtensions), new PropertyMetadata(null, OnItemsSourceChanged));

        public static void SetItemsSource(DependencyObject d, IEnumerable<MenuModel> value) => d.SetValue(ItemsSourceProperty, value);
        public static IEnumerable<MenuModel> GetItemsSource(DependencyObject d) => (IEnumerable<MenuModel>)d.GetValue(ItemsSourceProperty);

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MenuFlyout flyout)
            {
                // 核心逻辑：订阅 Opening 事件。
                // 这样无论 Children 怎么变，在点开右键的一瞬间都会重新生成 UI
                flyout.Opening -= Flyout_Opening;
                flyout.Opening += Flyout_Opening;

                // 初次构建
                RebuildItems(flyout);
            }
        }

        private static void Flyout_Opening(object sender, object e)
        {
            if (sender is MenuFlyout flyout)
            {
                RebuildItems(flyout);
            }
        }

        private static void RebuildItems(MenuFlyout flyout)
        {
            var items = GetItemsSource(flyout);
            if (items == null) return;

            // 获取被点击条目的 DataContext (ConnectionItem/AlbumItem)
            // 如果 Flyout 放在 Style 里，Target 就是 GridViewItem

            flyout.Items.Clear();
            foreach (var item in items)
            {
                var menuItem = CreateMenuItem(item);
                if (menuItem != null)
                    flyout.Items.Add(menuItem);
            }
        }

        private static MenuFlyoutItemBase CreateMenuItem(MenuModel model)
        {
            if (model == null) return null;

            // 处理子菜单 (Children)
            if (model.Children != null && model.Children.AsValueEnumerable().Any())
            {
                var subItem = new MenuFlyoutSubItem { Text = model.Title };

                // 递归创建子项
                foreach (var child in model.Children)
                {
                    var childItem = CreateMenuItem(child);
                    if (childItem != null)
                        subItem.Items.Add(childItem);
                }
                return subItem;
            }

            // 处理普通菜单项
            return new MenuFlyoutItem
            {
                Text = model.Title,
                Command = model.Command,
                CommandParameter = model.Tag
            };
        }
    }
}
