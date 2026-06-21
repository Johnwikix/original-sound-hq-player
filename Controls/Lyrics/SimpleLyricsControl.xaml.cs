using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Windows.Foundation;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class SimpleLyricsControl : UserControl
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;

        private List<LyricLine>? _lyrics;
        private List<LyricDisplayItem> _displayItems = new();
        private readonly Dictionary<LyricDisplayItem, (TextBlock LyricTb, TextBlock TransTb)> _itemMap = new();

        private int _currentLineIndex = -1;
        private CompositionPropertySet? _ps;
        private const string CurrentIndexKey = "CurrentIndex";
        private const float CurrentLineScale = 1.1f;
        private const float OtherLineScale = 1.0f;
        private const float ScaleTransitionDurationMs = 250f;
        private const double UserScrollCooldownSec = 3.0;
        private const int ScrollRetryMaxCount = 30;
        private const int ScrollRetryIntervalMs = 100;

        private double _cachedFontSize = 36.0;
        private string _cachedFontFamilyName = "Segoe UI";
        private TextAlignment _cachedTextAlignment = TextAlignment.Left;
        private double _cachedPlayingLineTopOffset = 0.35;
        private double _cachedUnplayedOpacity = 0.5;
        private double _cachedTranslatedOpacity = 0.6;
        private double _cachedOffsetMs;

        private bool _shutdown;
        private bool _isProgrammaticScrolling;

        private DispatcherQueueTimer? _autoScrollReturnTimer;
        private DispatcherQueueTimer? _scrollRetryTimer;
        private int _scrollRetryCount;

        public SimpleLyricsControl()
        {
            InitializeComponent();
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        public void PrepareForShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;

            TimeProgressBus.CurrentPlayingTimeChanged -= OnCurrentPlayingTimeChanged;
            OffsetMsBus.Changed -= OnOffsetMsChanged;
            LyricsFontSizeBus.Changed -= OnLyricsFontSizeChanged;
            UILyricsBus.Changed -= OnUILyricsChanged;
            LyricsSettingsBus.SyncRequested -= OnLyricsSettingsSync;
            LyricsSyncRequestBus.Requested -= OnLyricsSyncRequested;
            LyricList.ItemClick -= LyricList_ItemClick;
            LyricList.ContainerContentChanging -= LyricList_ContainerContentChanging;
            ScrollHost.ViewChanged -= ScrollHost_ViewChanged;
            ScrollHost.SizeChanged -= ScrollHost_SizeChanged;

            foreach (var item in _displayItems)
                item.PropertyChanged -= OnItemPropertyChanged;
            _itemMap.Clear();
            _displayItems.Clear();

            _autoScrollReturnTimer?.Stop();
            _autoScrollReturnTimer = null;
            _scrollRetryTimer?.Stop();
            _scrollRetryTimer = null;
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (_shutdown) return;

            TimeProgressBus.CurrentPlayingTimeChanged += OnCurrentPlayingTimeChanged;
            OffsetMsBus.Changed += OnOffsetMsChanged;
            LyricsFontSizeBus.Changed += OnLyricsFontSizeChanged;
            UILyricsBus.Changed += OnUILyricsChanged;
            LyricsSettingsBus.SyncRequested += OnLyricsSettingsSync;
            LyricsSyncRequestBus.Requested += OnLyricsSyncRequested;
            LyricList.ItemClick += LyricList_ItemClick;
            LyricList.ContainerContentChanging += LyricList_ContainerContentChanging;
            ScrollHost.ViewChanged += ScrollHost_ViewChanged;
            ScrollHost.SizeChanged += ScrollHost_SizeChanged;

            _ps = ElementCompositionPreview.GetElementVisual(this)?.Compositor?.CreatePropertySet();
            _ps?.InsertScalar(CurrentIndexKey, -1f);

            LyricsSyncRequestBus.Request();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            PrepareForShutdown();
        }

        private void OnLyricsSyncRequested()
        {
            _ps?.InsertScalar(CurrentIndexKey, (float)_currentLineIndex);
        }

        private void OnCurrentPlayingTimeChanged(long totalMs)
        {
            if (_lyrics is null || _lyrics.Count == 0) return;

            double effectiveMs = totalMs - _cachedOffsetMs;
            int newIndex = FindCurrentLineIndex(effectiveMs);
            if (newIndex == _currentLineIndex) return;

            int oldIndex = _currentLineIndex;
            _currentLineIndex = newIndex;
            _ps?.InsertScalar(CurrentIndexKey, (float)newIndex);

            UpdateIsCurrent(oldIndex, newIndex);
            ScheduleScrollToCurrent();
        }

        private void OnOffsetMsChanged(double value)
        {
            if (Math.Abs(_cachedOffsetMs - value) < 0.5) return;
            _cachedOffsetMs = value;
        }

        private void OnLyricsFontSizeChanged(double value)
        {
            if (Math.Abs(_cachedFontSize - value) < 0.5) return;
            _cachedFontSize = value;
            foreach (var item in _displayItems)
                item.DisplayFontSize = value;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScheduleScrollToCurrent);
        }

        private void OnUILyricsChanged(IList<LyricLine>? value)
        {
            foreach (var item in _displayItems)
                item.PropertyChanged -= OnItemPropertyChanged;
            _itemMap.Clear();

            _displayItems = new List<LyricDisplayItem>();

            if (value is null)
            {
                _lyrics = null;
            }
            else if (value is List<LyricLine> list)
            {
                _lyrics = list;
            }
            else
            {
                _lyrics = new List<LyricLine>(value);
            }

            _currentLineIndex = -1;

            BuildDisplayItems();
            MatchCurrentLineFromTime(_cachedOffsetMs, preferLatest: true);
            LyricList.ItemsSource = _displayItems;
            if (ScrollHost.ActualWidth > 0)
                LyricList.Width = ScrollHost.ActualWidth;
            _ps?.InsertScalar(CurrentIndexKey, -1f);

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                ScheduleScrollToCurrent();
            });
        }

        private void OnLyricsSettingsSync(LyricsSettingsBus.Settings s)
        {
            _cachedFontFamilyName = s.FontFamilyName;
            _cachedTextAlignment = CanvasToTextAlignment(s.LyricsTextAlignment);
            _cachedUnplayedOpacity = s.UnplayedOpacity;
            _cachedTranslatedOpacity = s.TranslatedOpacity;
            _cachedPlayingLineTopOffset = s.PlayingLineTopOffset;

            foreach (var item in _displayItems)
            {
                item.DisplayFontFamily = _cachedFontFamilyName;
                item.DisplayTextAlignment = _cachedTextAlignment;
                item.DisplayTranslationOpacity = _cachedTranslatedOpacity;
            }

            // 触发当前行的 IsCurrent 翻转（false→true），让订阅回调统一刷新所有 item 的主歌词 Opacity
            if (_currentLineIndex >= 0 && _currentLineIndex < _displayItems.Count)
            {
                var current = _displayItems[_currentLineIndex];
                current.IsCurrent = false;
                current.IsCurrent = true;
            }
            else
            {
                for (int i = 0; i < _displayItems.Count; i++)
                    _displayItems[i].IsCurrent = _displayItems[i].IsCurrent;
            }

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScheduleScrollToCurrent);
        }

        private static TextAlignment CanvasToTextAlignment(CanvasHorizontalAlignment a) => a switch
        {
            CanvasHorizontalAlignment.Center => TextAlignment.Center,
            CanvasHorizontalAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

        private int FindCurrentLineIndex(double effectiveMs)
        {
            var lyrics = _lyrics;
            if (lyrics is null || lyrics.Count == 0) return -1;

            int lo = 0, hi = lyrics.Count - 1;
            int matched = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (lyrics[mid].StartMs <= effectiveMs)
                {
                    matched = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return matched;
        }

        private void MatchCurrentLineFromTime(double fromMs, bool preferLatest)
        {
            if (_lyrics is null || _lyrics.Count == 0) return;
            int idx = FindCurrentLineIndex(fromMs);
            int oldIndex = _currentLineIndex;
            _currentLineIndex = idx;
            _ps?.InsertScalar(CurrentIndexKey, (float)idx);
            if (idx != oldIndex) UpdateIsCurrent(oldIndex, idx);
        }

        private void UpdateIsCurrent(int oldIndex, int newIndex)
        {
            if (oldIndex >= 0 && oldIndex < _displayItems.Count)
                _displayItems[oldIndex].IsCurrent = false;
            if (newIndex >= 0 && newIndex < _displayItems.Count)
                _displayItems[newIndex].IsCurrent = true;
        }

        private void BuildDisplayItems()
        {
            _displayItems.Clear();
            if (_lyrics is null) return;

            for (int i = 0; i < _lyrics.Count; i++)
            {
                var line = _lyrics[i];
                var mainText = ConcatWords(line.Words);
                var item = new LyricDisplayItem(line, i, mainText, line.TransLateText ?? string.Empty)
                {
                    DisplayFontSize = _cachedFontSize,
                    DisplayFontFamily = _cachedFontFamilyName,
                    DisplayTextAlignment = _cachedTextAlignment,
                    DisplayOpacity = _cachedUnplayedOpacity,
                    DisplayTranslationOpacity = _cachedTranslatedOpacity,
                };
                item.PropertyChanged += OnItemPropertyChanged;
                _displayItems.Add(item);
            }
        }

        private static string ConcatWords(IList<LyricWord> words)
        {
            if (words.Count == 0) return string.Empty;
            if (words.Count == 1) return words[0].Word ?? string.Empty;
            var sb = new StringBuilder(words.Count * 6);
            foreach (var w in words)
            {
                if (!string.IsNullOrEmpty(w.Word))
                    sb.Append(w.Word);
            }
            return sb.ToString();
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not LyricDisplayItem item) return;
            if (!_itemMap.TryGetValue(item, out var pair)) return;
            ApplyItemToBlocks(item, pair.LyricTb, pair.TransTb);
            if (e.PropertyName == nameof(LyricDisplayItem.IsCurrent))
            {
                AnimateContainerScale(item.LineIndex, item.IsCurrent ? CurrentLineScale : OtherLineScale);
            }
            else if (e.PropertyName == nameof(LyricDisplayItem.DisplayTextAlignment))
            {
                AnimateContainerScale(item.LineIndex, item.IsCurrent ? CurrentLineScale : OtherLineScale, instant: true);
            }
        }

        private void ApplyItemToBlocks(LyricDisplayItem item, TextBlock lyricTb, TextBlock transTb)
        {
            lyricTb.Text = item.MainText;
            lyricTb.FontSize = item.DisplayFontSize;
            lyricTb.TextAlignment = item.DisplayTextAlignment;
            if (!string.IsNullOrEmpty(item.DisplayFontFamily))
                lyricTb.FontFamily = new FontFamily(item.DisplayFontFamily);
            lyricTb.Opacity = item.IsCurrent ? 1.0 : _cachedUnplayedOpacity;

            transTb.Text = item.TranslationText;
            transTb.Visibility = item.HasTranslation ? Visibility.Visible : Visibility.Collapsed;
            transTb.FontSize = item.DisplayFontSize * 0.75;
            transTb.TextAlignment = item.DisplayTextAlignment;
            if (!string.IsNullOrEmpty(item.DisplayFontFamily))
                transTb.FontFamily = new FontFamily(item.DisplayFontFamily);
            transTb.Opacity = item.DisplayTranslationOpacity;
        }

        private void LyricList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue) return;
            if (args.Item is not LyricDisplayItem item) return;
            int idx = args.ItemIndex;
            if (idx < 0 || idx >= _displayItems.Count) return;

            var border = args.ItemContainer?.ContentTemplateRoot as Border;
            if (border is null) return;
            var panel = border.Child as StackPanel;
            if (panel is null || panel.Children.Count < 2) return;
            var lyricTb = panel.Children[0] as TextBlock;
            var transTb = panel.Children[1] as TextBlock;
            if (lyricTb is null || transTb is null) return;

            _itemMap[item] = (lyricTb, transTb);
            item.PropertyChanged -= OnItemPropertyChanged;
            item.PropertyChanged += OnItemPropertyChanged;

            ApplyItemToBlocks(item, lyricTb, transTb);

            var itemContainer = args.ItemContainer as ListViewItem;
            if (item.IsCurrent)
                AnimateContainerScale(idx, CurrentLineScale, instant: true, container: itemContainer);
        }

        private void AnimateContainerScale(int index, float targetScale, bool instant = false, ListViewItem? container = null)
        {
            if (index < 0 || index >= _displayItems.Count) return;
            container ??= LyricList.ContainerFromIndex(index) as ListViewItem;
            if (container is null) return;

            var visual = ElementCompositionPreview.GetElementVisual(container);
            if (visual is null) return;
            var compositor = visual.Compositor;
            if (compositor is null) return;

            float currentScale = visual.Scale.X;
            if (Math.Abs(currentScale - targetScale) < 0.005f) return;

            var alignment = _displayItems[index].DisplayTextAlignment;
            float containerW = (float)LyricList.ActualWidth;
            float centerX = alignment switch
            {
                TextAlignment.Center => containerW / 2f,
                TextAlignment.Right => containerW,
                _ => 0f,
            };
            visual.CenterPoint = new System.Numerics.Vector3(centerX, visual.Size.Y / 2f, 0f);

            if (instant)
            {
                visual.Scale = new System.Numerics.Vector3(targetScale);
                return;
            }

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0f, currentScale);
            anim.InsertKeyFrame(1f, targetScale);
            anim.Duration = TimeSpan.FromMilliseconds(ScaleTransitionDurationMs);
            visual.StartAnimation("Scale.X", anim);
            visual.StartAnimation("Scale.Y", anim);
        }

        private void ScheduleScrollToCurrent()
        {
            if (_currentLineIndex < 0)
            {
                StopScrollRetry();
                return;
            }
            _scrollRetryCount = 0;
            _scrollRetryTimer ??= DispatcherQueue.CreateTimer();
            _scrollRetryTimer.Interval = TimeSpan.FromMilliseconds(ScrollRetryIntervalMs);
            _scrollRetryTimer.Tick -= OnScrollRetryTick;
            _scrollRetryTimer.Tick += OnScrollRetryTick;
            _scrollRetryTimer.Start();
        }

        private void OnScrollRetryTick(DispatcherQueueTimer sender, object args)
        {
            if (TryScrollToCurrentLine())
            {
                sender.Stop();
                return;
            }
            if (++_scrollRetryCount >= ScrollRetryMaxCount)
            {
                sender.Stop();
            }
        }

        private void StopScrollRetry()
        {
            _scrollRetryTimer?.Stop();
            _scrollRetryCount = 0;
        }

        private bool TryScrollToCurrentLine()
        {
            if (_currentLineIndex < 0 || _currentLineIndex >= _displayItems.Count) return false;
            if (ScrollHost.ActualHeight <= 0) return false;

            if (LyricList.ContainerFromIndex(_currentLineIndex) is not UIElement container) return false;
            if (container.RenderSize.Height <= 0) return false;

            var topInViewport = container
                .TransformToVisual(ScrollHost)
                .TransformPoint(new Point(0, 0))
                .Y;
            double centerInViewport = topInViewport + container.RenderSize.Height / 2.0;
            double targetOffset = ScrollHost.VerticalOffset
                + centerInViewport
                - ScrollHost.ActualHeight * _cachedPlayingLineTopOffset;
            if (double.IsNaN(targetOffset) || double.IsInfinity(targetOffset)) return false;
            targetOffset = Math.Max(0, targetOffset);

            _isProgrammaticScrolling = true;
            ScrollHost.ChangeView(null, targetOffset, null, disableAnimation: false);
            return true;
        }

        private void ScrollHost_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isProgrammaticScrolling)
            {
                if (!e.IsIntermediate) _isProgrammaticScrolling = false;
                return;
            }

            if (!e.IsIntermediate)
            {
                _autoScrollReturnTimer ??= DispatcherQueue.CreateTimer();
                _autoScrollReturnTimer.Interval = TimeSpan.FromSeconds(UserScrollCooldownSec);
                _autoScrollReturnTimer.Tick -= OnAutoScrollReturnTick;
                _autoScrollReturnTimer.Tick += OnAutoScrollReturnTick;
                _autoScrollReturnTimer.Start();
            }
        }

        private void ScrollHost_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            var w = e.NewSize.Width;
            if (w <= 0 || _displayItems.Count == 0) return;
            LyricList.Width = w;
        }

        private void OnAutoScrollReturnTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            TryScrollToCurrentLine();
        }

        private void LyricList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not LyricDisplayItem item) return;
            if (item.Source is null) return;

            _autoScrollReturnTimer?.Stop();
            LyricLineClicked?.Invoke(this, TimeSpan.FromMilliseconds(item.Source.StartMs));
        }
    }
}
