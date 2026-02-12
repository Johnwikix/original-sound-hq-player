using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using WinUIMusicPlayer.Model;

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
                flyout.Items.Clear();
                if (e.NewValue is IEnumerable<MenuModel> items)
                {
                    foreach (var item in items)
                    {
                        flyout.Items.Add(CreateMenuItem(item));
                    }
                }
            }
        }

        private static MenuFlyoutItemBase CreateMenuItem(MenuModel model)
        {
            if (model.Children != null && model.Children.Any())
            {
                var subItem = new MenuFlyoutSubItem { Text = model.Title }; // 这里可以用资源加载器处理 Uid
                foreach (var child in model.Children)
                {
                    subItem.Items.Add(CreateMenuItem(child));
                }
                return subItem;
            }

            var menuItem = new MenuFlyoutItem
            {
                Text = model.Title,
                Command = model.Command,
                CommandParameter = model.Tag
            };
            return menuItem;
        }
    }
}
