using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Numerics;
using Windows.Foundation;

namespace WinUIMusicPlayer.Behaviors
{
    public class ScrollBasedOpacityBehavior : Behavior<ScrollView>
    {
        public static readonly DependencyProperty TargetListViewProperty =
            DependencyProperty.Register(
                nameof(TargetListView),
                typeof(ListView),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(null, OnTargetListViewChanged));

        public static readonly DependencyProperty MaxOpacityProperty =
            DependencyProperty.Register(
                nameof(MaxOpacity),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(0.6));

        public static readonly DependencyProperty MinOpacityProperty =
            DependencyProperty.Register(
                nameof(MinOpacity),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(0.01));

        public static readonly DependencyProperty MaxDistanceRatioProperty =
            DependencyProperty.Register(
                nameof(MaxDistanceRatio),
                typeof(double),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata(2.2));

        public static readonly DependencyProperty TargetElementNameProperty =
            DependencyProperty.Register(
                nameof(TargetElementName),
                typeof(string),
                typeof(ScrollBasedOpacityBehavior),
                new PropertyMetadata("LyricsTextBlockBase"));

        public ListView TargetListView
        {
            get => (ListView)GetValue(TargetListViewProperty);
            set => SetValue(TargetListViewProperty, value);
        }

        public double MaxOpacity
        {
            get => (double)GetValue(MaxOpacityProperty);
            set => SetValue(MaxOpacityProperty, value);
        }

        public double MinOpacity
        {
            get => (double)GetValue(MinOpacityProperty);
            set => SetValue(MinOpacityProperty, value);
        }

        public double MaxDistanceRatio
        {
            get => (double)GetValue(MaxDistanceRatioProperty);
            set => SetValue(MaxDistanceRatioProperty, value);
        }

        public string TargetElementName
        {
            get => (string)GetValue(TargetElementNameProperty);
            set => SetValue(TargetElementNameProperty, value);
        }

        // 缓存的变量
        private ItemsStackPanel _cachedPanel;
        private UIElement _cachedScrollContent;
        private double _cachedMaxDistance = 0;
        private double _cachedOpacityRange = 0.59;

        private Dictionary<int, Visual> _visualCache = new Dictionary<int, Visual>();
        private Dictionary<int, TextBlock> _targetElementCache = new Dictionary<int, TextBlock>();
        // 之前的 ListView 引用，用于取消订阅
        private ListView _previousListView;
        private readonly Point _reusePoint = new Point(0, 0);

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject is not null)
            {
                AssociatedObject.ViewChanged += OnViewChanged;
                AssociatedObject.SizeChanged += OnSizeChanged;
            }

            // 如果已经有 TargetListView，立即订阅
            if (TargetListView is not null)
            {
                SubscribeToListView(TargetListView);
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject is not null)
            {
                AssociatedObject.ViewChanged -= OnViewChanged;
                AssociatedObject.SizeChanged -= OnSizeChanged;
            }

            // 取消订阅
            UnsubscribeFromListView(_previousListView);
            _targetElementCache.Clear();
            _visualCache.Clear();
        }

        private static void OnTargetListViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var behavior = d as ScrollBasedOpacityBehavior;
            if (behavior is null) return;

            // 取消之前的订阅
            if (e.OldValue is ListView oldListView)
            {
                behavior.UnsubscribeFromListView(oldListView);
            }

            // 订阅新的 ListView
            if (e.NewValue is ListView newListView)
            {
                behavior.SubscribeToListView(newListView);
            }
        }

        private void SubscribeToListView(ListView listView)
        {
            if (listView is null) return;

            _previousListView = listView;

            // 订阅 ItemsSource 变化（如果是 INotifyCollectionChanged）
            if (listView.ItemsSource is INotifyCollectionChanged observableCollection)
            {
                observableCollection.CollectionChanged += OnItemsSourceCollectionChanged;
            }
            // 订阅容器准备和清理事件
            listView.ContainerContentChanging += OnContainerContentChanging;
            // 订阅 Loaded 事件，确保容器已生成
            listView.Loaded += OnListViewLoaded;
        }

        private void UnsubscribeFromListView(ListView listView)
        {
            if (listView is null) return;

            if (listView.ItemsSource is INotifyCollectionChanged observableCollection)
            {
                observableCollection.CollectionChanged -= OnItemsSourceCollectionChanged;
            }

            listView.ContainerContentChanging -= OnContainerContentChanging;
            listView.Loaded -= OnListViewLoaded;

            _targetElementCache.Clear();
            _visualCache.Clear();
        }

        private void OnListViewLoaded(object sender, RoutedEventArgs e)
        {
            // ListView 加载完成后，刷新容器缓存
            RefreshContainerCache();
        }

        private void OnItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // ItemsSource 集合变化时刷新缓存
            RefreshContainerCache();
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // 容器被回收或重用时更新缓存
            if (args.InRecycleQueue)
            {
                // 容器被回收，从缓存中移除
                if (args.ItemIndex >= 0)
                {
                    _targetElementCache.Remove(args.ItemIndex);
                    _visualCache.Remove(args.ItemIndex);
                }
            }
            else
            {
                // 容器被准备或重用，更新缓存
                if (args.ItemContainer is ListViewItem item && args.ItemIndex >= 0)
                {
                    var targetElement = FindElementByName(item, TargetElementName) as TextBlock;
                    if (targetElement is not null)
                    {
                        _targetElementCache[args.ItemIndex] = targetElement;
                        _visualCache[args.ItemIndex] = ElementCompositionPreview.GetElementVisual(targetElement);
                    }
                }
            }
        }

        private void RefreshContainerCache()
        {
            if (TargetListView is null) return;

            _targetElementCache.Clear();
            _visualCache.Clear();
            _cachedPanel = TargetListView.ItemsPanelRoot as ItemsStackPanel;

            if (_cachedPanel is null) return;

            for (int i = _cachedPanel.FirstVisibleIndex; i <= _cachedPanel.LastVisibleIndex; i++)
            {
                var itemContainer = TargetListView.ContainerFromIndex(i) as ListViewItem;
                if (itemContainer is not null)
                {
                    var targetElement = FindElementByName(itemContainer, TargetElementName) as TextBlock;
                    if (targetElement is not null)
                    {
                        _targetElementCache[i] = targetElement;
                        _visualCache[i] = ElementCompositionPreview.GetElementVisual(targetElement);
                    }
                }
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCachedValues();
        }

        private void OnViewChanged(ScrollView sender, object args)
        {
            UpdateOpacity();
        }

        private void UpdateCachedValues()
        {
            if (AssociatedObject is null || TargetListView is null) return;
            _cachedPanel = TargetListView.ItemsPanelRoot as ItemsStackPanel;
            _cachedScrollContent = AssociatedObject.Content as UIElement;
            _cachedMaxDistance = AssociatedObject.ActualHeight / MaxDistanceRatio;
            _cachedOpacityRange = MaxOpacity - MinOpacity;
        }

        private void UpdateOpacity()
        {
            if (AssociatedObject is null || TargetListView is null || _cachedPanel is null || _cachedScrollContent is null)
                return;

            double viewerCenter = AssociatedObject.VerticalOffset + (AssociatedObject.ActualHeight * 0.5);

            for (int i = _cachedPanel.FirstVisibleIndex; i <= _cachedPanel.LastVisibleIndex; i++)
            {
                if (!_targetElementCache.TryGetValue(i, out var targetElement) || targetElement == null)
                    continue;

                double itemCenter = targetElement.TransformToVisual(_cachedScrollContent).TransformPoint(_reusePoint).Y + (targetElement.ActualHeight * 0.5);
                double distance = Math.Abs(itemCenter - viewerCenter);
                float opacity = distance >= _cachedMaxDistance
                    ? (float)MinOpacity
                    : (float)(MaxOpacity - ((distance / _cachedMaxDistance) * _cachedOpacityRange));

                if (_visualCache.TryGetValue(i, out var visual) && visual is not null)
                {
                    visual.Opacity = opacity;
                }
            }
        }

        private FrameworkElement FindElementByName(DependencyObject parent, string name)
        {
            if (parent is FrameworkElement fe && fe.Name == name)
                return fe;

            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var result = FindElementByName(child, name);
                if (result != null) return result;
            }

            return null;
        }
    }
}