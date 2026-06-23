using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using Windows.Foundation;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class SimpleLyricsControl : UserControl
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;

        private List<LyricLine>? _lyrics;
        private List<LyricDisplayItem> _displayItems = new();
        private readonly Dictionary<LyricDisplayItem, (Border Border, TextBlock LyricTb, TextBlock TransTb)> _itemMap = new();

        private int _currentLineIndex = -1;
        private int _hoveredIndex = -1;
        private CompositionPropertySet? _ps;
        private const string CurrentIndexKey = "CurrentIndex";
        private const float CurrentLineScale = 1.1f;
        private const float OtherLineScale = 1.0f;
        private const float ScaleTransitionDurationMs = 400f;
        private const double UserScrollCooldownSec = 3.0;
        private const double ScrollAlignTolerancePx = 8.0;
        private const int ScrollRetryMaxCount = 30;
        private const int ScrollRetryIntervalMs = 100;

        private const float BlurOffscreenX = -100000f;
        private const float BlurTransitionDurationMs = ScaleTransitionDurationMs;
        private CompositionEffectFactory? _blurFactory;
        private double _cachedLyricsBlurAmount;
        private readonly Dictionary<Border, BlurResources> _blurMap = new();
        private readonly Dictionary<ListViewItem, float> _containerScaleTarget = new();
        private ScalarKeyFrameAnimation? _reusableScaleAnim;
        private ScalarKeyFrameAnimation? _reusableBlurAnim;

        private double _cachedFontSize = 36.0;
        private string _cachedFontFamilyName = "Segoe UI";
        private TextAlignment _cachedTextAlignment = TextAlignment.Left;
        private double _cachedPlayingLineTopOffset = 0.4;
        private double _cachedUnplayedOpacity = 0.5;
        private double _cachedTranslatedOpacity = 0.6;
        private double _cachedOffsetMs;

        private bool _shutdown;
        private bool _isProgrammaticScrolling;
        private double _lastProgrammaticOffset = double.NaN;
        private bool _manualBrowsing;
        private ScrollPresenter? _scrollPresenter;

        private DispatcherQueueTimer? _autoScrollReturnTimer;
        private DispatcherQueueTimer? _scrollRetryTimer;
        private int _scrollRetryCount;

        private FontFamily? _fontFamilyCache;
        private string? _fontFamilyCacheName;

        private readonly PropertyChangedEventHandler _onItemPropertyChanged;
        private readonly TypedEventHandler<DispatcherQueueTimer, object> _onScrollRetryTick;
        private readonly TypedEventHandler<DispatcherQueueTimer, object> _onAutoScrollReturnTick;
        private readonly DispatcherQueueHandler _scheduleScrollAction;
        private readonly PointerEventHandler _onScrollPointerMoved;
        private readonly PointerEventHandler _onScrollPointerExited;
        private readonly SizeChangedEventHandler _onBlurPanelSizeChanged;
        private readonly DispatcherQueueHandler _refreshBlurGeometryAction;
        private bool _blurGeometryRefreshQueued;

        public SimpleLyricsControl()
        {
            InitializeComponent();
            _onItemPropertyChanged = OnItemPropertyChanged;
            _onScrollRetryTick = OnScrollRetryTick;
            _onAutoScrollReturnTick = OnAutoScrollReturnTick;
            _scheduleScrollAction = ScheduleScrollToCurrent;
            _onScrollPointerMoved = OnScrollPointerMoved;
            _onScrollPointerExited = OnScrollPointerExited;
            _onBlurPanelSizeChanged = OnBlurPanelSizeChanged;
            _refreshBlurGeometryAction = RefreshBlurGeometryAll;
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private FontFamily GetFontFamily(string name)
        {
            if (_fontFamilyCache is null || _fontFamilyCacheName != name)
            {
                _fontFamilyCache = new FontFamily(name);
                _fontFamilyCacheName = name;
            }
            return _fontFamilyCache;
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
            if (_scrollPresenter is not null)
            {
                _scrollPresenter.ViewChanged -= ScrollPresenter_ViewChanged;
                _scrollPresenter = null;
            }
            ScrollHost.SizeChanged -= ScrollHost_SizeChanged;
            ScrollHost.RemoveHandler(UIElement.PointerMovedEvent, _onScrollPointerMoved);
            ScrollHost.RemoveHandler(UIElement.PointerExitedEvent, _onScrollPointerExited);

            foreach (var item in _displayItems)
                item.PropertyChanged -= _onItemPropertyChanged;
            _itemMap.Clear();
            _displayItems.Clear();
            _hoveredIndex = -1;
            _containerScaleTarget.Clear();

            TeardownAllBlur();
            _blurFactory?.Dispose();
            _blurFactory = null;
            _reusableScaleAnim?.Dispose();
            _reusableScaleAnim = null;
            _reusableBlurAnim?.Dispose();
            _reusableBlurAnim = null;

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
            ScrollHost.SizeChanged += ScrollHost_SizeChanged;
            ScrollHost.AddHandler(UIElement.PointerMovedEvent, _onScrollPointerMoved, true);
            ScrollHost.AddHandler(UIElement.PointerExitedEvent, _onScrollPointerExited, true);

            _scrollPresenter = ((ScrollView)ScrollHost).ScrollPresenter;
            if (_scrollPresenter is not null)
                _scrollPresenter.ViewChanged += ScrollPresenter_ViewChanged;

            _ps = ElementCompositionPreview.GetElementVisual(this)?.Compositor?.CreatePropertySet();
            _ps?.InsertScalar(CurrentIndexKey, -1f);

            EnsureBlurFactory();

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
            value *= 0.8;
            if (Math.Abs(_cachedFontSize - value) < 0.5) return;
            _cachedFontSize = value;
            foreach (var item in _displayItems)
                item.DisplayFontSize = value;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _scheduleScrollAction);
        }

        private void OnUILyricsChanged(IList<LyricLine>? value)
        {
            foreach (var item in _displayItems)
                item.PropertyChanged -= _onItemPropertyChanged;
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
            _hoveredIndex = -1;
            _manualBrowsing = false;

            BuildDisplayItems();
            MatchCurrentLineFromTime(_cachedOffsetMs, preferLatest: true);
            LyricList.ItemsSource = _displayItems;
            if (ScrollHost.ActualWidth > 0)
                LyricList.Width = ScrollHost.ActualWidth;
            _ps?.InsertScalar(CurrentIndexKey, -1f);

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _scheduleScrollAction);
        }

        private void OnLyricsSettingsSync(LyricsSettingsBus.Settings s)
        {
            _cachedFontFamilyName = s.FontFamilyName;
            _cachedTextAlignment = CanvasToTextAlignment(s.LyricsTextAlignment);
            _cachedUnplayedOpacity = s.UnplayedOpacity;
            _cachedTranslatedOpacity = s.TranslatedOpacity;
            _cachedPlayingLineTopOffset = s.PlayingLineTopOffset;

            double newBlur = Math.Max(0, s.LyricsBlurAmount);
            newBlur *= 0.33;
            bool blurChanged = Math.Abs(newBlur - _cachedLyricsBlurAmount) > 0.01;
            _cachedLyricsBlurAmount = newBlur;

            foreach (var item in _displayItems)
            {
                item.DisplayFontFamily = _cachedFontFamilyName;
                item.DisplayTextAlignment = _cachedTextAlignment;
                item.DisplayTranslationOpacity = _cachedTranslatedOpacity;
            }

            // 直接刷新所有已实现行（主歌词 Opacity 取最新 _cachedUnplayedOpacity），
            // 不再依赖 IsCurrent 翻转副作用（仅改 UnplayedOpacity 时不会触发 PropertyChanged）。
            foreach (var kv in _itemMap)
                ApplyItemToBlocks(kv.Key, kv.Value.LyricTb, kv.Value.TransTb);

            if (blurChanged) RefreshAllBlur();

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _scheduleScrollAction);
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
            if (_hoveredIndex == newIndex) _hoveredIndex = -1;
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
                item.PropertyChanged += _onItemPropertyChanged;
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
                ApplyEmphasis(item.LineIndex, animate: true);
            }
            else if (e.PropertyName == nameof(LyricDisplayItem.DisplayTextAlignment))
            {
                ApplyBorderSpacing(pair.Border, item.DisplayTextAlignment);
                AnimateContainerScale(item.LineIndex, item.IsCurrent ? CurrentLineScale : OtherLineScale, instant: true);
                if (_blurMap.TryGetValue(pair.Border, out var res))
                    UpdateBlurGeometry(res);
            }
            else if (e.PropertyName == nameof(LyricDisplayItem.DisplayFontSize))
            {
                ApplyBorderSpacing(pair.Border, item.DisplayTextAlignment);
                if (_blurMap.TryGetValue(pair.Border, out var res))
                    UpdateBlurGeometry(res);
            }
        }

        private void ApplyItemToBlocks(LyricDisplayItem item, TextBlock lyricTb, TextBlock transTb)
        {
            lyricTb.Text = item.MainText;
            lyricTb.FontSize = item.DisplayFontSize;
            lyricTb.TextAlignment = item.DisplayTextAlignment;
            if (!string.IsNullOrEmpty(item.DisplayFontFamily))
                lyricTb.FontFamily = GetFontFamily(item.DisplayFontFamily);
            lyricTb.Opacity = item.IsCurrent ? 1.0 : _cachedUnplayedOpacity;

            transTb.Text = item.TranslationText;
            transTb.Visibility = item.HasTranslation ? Visibility.Visible : Visibility.Collapsed;
            transTb.FontSize = item.DisplayFontSize * 0.75;
            transTb.TextAlignment = item.DisplayTextAlignment;
            if (!string.IsNullOrEmpty(item.DisplayFontFamily))
                transTb.FontFamily = GetFontFamily(item.DisplayFontFamily);
            transTb.Opacity = item.DisplayTranslationOpacity;
        }

        private void ApplyBorderSpacing(Border border, TextAlignment alignment)
        {
            double v = _cachedFontSize * 0.5;
            double full = _cachedFontSize * 1.5;
            double mid = _cachedFontSize * 0.75;
            double left, right;
            switch (alignment)
            {
                case TextAlignment.Center:
                    left = mid;
                    right = mid;
                    break;
                case TextAlignment.Right:
                    left = full;
                    right = 10;
                    break;
                default:
                    left = 10;
                    right = full;
                    break;
            }
            var margin = new Thickness(0, 0.5 * v, 0, 0.5 * v);
            if (!border.Margin.Equals(margin)) border.Margin = margin;
            var padding = new Thickness(left, v, right, v);
            if (!border.Padding.Equals(padding)) border.Padding = padding;
        }

        private void LyricList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer is ListViewItem recycledContainer)
                    _containerScaleTarget.Remove(recycledContainer);
                if (args.ItemContainer?.ContentTemplateRoot is Border recycled)
                {
                    if (recycled.Tag is LyricDisplayItem oldItem && oldItem.LineIndex == _hoveredIndex)
                        _hoveredIndex = -1;
                    TeardownBlurForBorder(recycled);
                }
                return;
            }
            if (args.Item is not LyricDisplayItem item) return;
            int idx = args.ItemIndex;
            if (idx < 0 || idx >= _displayItems.Count) return;

            if (args.ItemContainer?.ContentTemplateRoot is not Border border) return;
            ApplyBorderSpacing(border, item.DisplayTextAlignment);
            if (border.Child is not StackPanel panel || panel.Children.Count < 2) return;
            if (panel.Children[0] is not TextBlock lyricTb || panel.Children[1] is not TextBlock transTb) return;

            _itemMap[item] = (border, lyricTb, transTb);
            item.PropertyChanged -= _onItemPropertyChanged;
            item.PropertyChanged += _onItemPropertyChanged;

            ApplyItemToBlocks(item, lyricTb, transTb);

            var itemContainer = args.ItemContainer as ListViewItem;

            if (_cachedLyricsBlurAmount > 0)
                BuildBlurForContainer(border, panel, item.IsCurrent);
            else
                TeardownBlurForBorder(border);

            border.Tag = item;
            ApplyEmphasis(idx, animate: false, container: itemContainer);
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

            float lastTarget = _containerScaleTarget.TryGetValue(container, out var t) ? t : visual.Scale.X;
            if (Math.Abs(lastTarget - targetScale) < 0.005f) return;
            _containerScaleTarget[container] = targetScale;

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

            _reusableScaleAnim ??= compositor.CreateScalarKeyFrameAnimation();
            _reusableScaleAnim.InsertKeyFrame(1f, targetScale);
            _reusableScaleAnim.Duration = TimeSpan.FromMilliseconds(ScaleTransitionDurationMs);
            visual.StartAnimation("Scale.X", _reusableScaleAnim);
            visual.StartAnimation("Scale.Y", _reusableScaleAnim);
        }

        private void ScheduleScrollToCurrent()
        {
            if (_manualBrowsing)
            {
                StopScrollRetry();
                return;
            }
            if (_currentLineIndex < 0)
            {
                StopScrollRetry();
                return;
            }
            _scrollRetryCount = 0;
            _scrollRetryTimer ??= DispatcherQueue.CreateTimer();
            _scrollRetryTimer.Interval = TimeSpan.FromMilliseconds(ScrollRetryIntervalMs);
            _scrollRetryTimer.Tick -= _onScrollRetryTick;
            _scrollRetryTimer.Tick += _onScrollRetryTick;
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
            if (_scrollPresenter is null) return false;

            if (LyricList.ContainerFromIndex(_currentLineIndex) is not UIElement container) return false;
            if (container.RenderSize.Height <= 0) return false;

            var topInViewport = container
                .TransformToVisual(ScrollHost)
                .TransformPoint(new Point(0, 0))
                .Y;
            double centerInViewport = topInViewport + container.RenderSize.Height / 2.0;
            double targetOffset = _scrollPresenter.VerticalOffset
                + centerInViewport
                - ScrollHost.ActualHeight * _cachedPlayingLineTopOffset;
            if (double.IsNaN(targetOffset) || double.IsInfinity(targetOffset)) return false;
            targetOffset = Math.Max(0, targetOffset);
            _lastProgrammaticOffset = targetOffset;

            // 自动滚动（换行的程序化滚动 / 冷却回正）发生时恢复模糊
            ExitManualBrowsing();

            if (Math.Abs(_scrollPresenter.VerticalOffset - targetOffset) < ScrollAlignTolerancePx)
                return true;

            _isProgrammaticScrolling = true;
            _scrollPresenter.ScrollTo(0, targetOffset, new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
            return true;
        }

        private void ScrollPresenter_ViewChanged(ScrollPresenter sender, object args)
        {
            // WinAppSDK 2.2 中 ScrollPresenter.ViewChanged 事件参数为 object/IInspectable，
            // 用 ScrollPresenter.State != Idle 替代原 ScrollViewerViewChangedEventArgs.IsIntermediate。
            bool isIntermediate = sender.State != ScrollingInteractionState.Idle;

            if (_isProgrammaticScrolling)
            {
                if (!isIntermediate) _isProgrammaticScrolling = false;
                return;
            }

            // 程序化滚动后的 settling 余波（落点贴近上次程序化目标）：忽略，不算用户滚动
            if (_scrollPresenter is not null &&
                !double.IsNaN(_lastProgrammaticOffset) &&
                Math.Abs(_scrollPresenter.VerticalOffset - _lastProgrammaticOffset) < ScrollAlignTolerancePx)
                return;

            // 真·用户滚动：一开始拖动（含 intermediate）就取消所有行模糊
            EnterManualBrowsing();

            if (!isIntermediate)
            {
                _autoScrollReturnTimer ??= DispatcherQueue.CreateTimer();
                _autoScrollReturnTimer.Interval = TimeSpan.FromSeconds(UserScrollCooldownSec);
                _autoScrollReturnTimer.Tick -= _onAutoScrollReturnTick;
                _autoScrollReturnTimer.Tick += _onAutoScrollReturnTick;
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

        private void ApplyEmphasis(int index, bool animate, ListViewItem? container = null)
        {
            if (index < 0 || index >= _displayItems.Count) return;
            bool isCurrent = index == _currentLineIndex;
            bool isHovered = index == _hoveredIndex;
            float scale = isCurrent ? CurrentLineScale : OtherLineScale;

            container ??= LyricList.ContainerFromIndex(index) as ListViewItem;
            AnimateContainerScale(index, scale, instant: !animate, container: container);

            if (container?.ContentTemplateRoot is Border border && _blurMap.TryGetValue(border, out var res))
            {
                float target = (_manualBrowsing || isCurrent || isHovered) ? 0f : (float)_cachedLyricsBlurAmount;
                if (animate) AnimateBlurAmount(res, target);
                else SetBlurAmountInstant(res, target);
            }
        }

        private void EnterManualBrowsing()
        {
            if (_manualBrowsing) return;
            _manualBrowsing = true;
            StopScrollRetry();
            RefreshAllLinesBlur();
        }

        private void ExitManualBrowsing()
        {
            if (!_manualBrowsing) return;
            _manualBrowsing = false;
            RefreshAllLinesBlur();
        }

        private void RefreshAllLinesBlur()
        {
            foreach (var kv in _blurMap)
            {
                var res = kv.Value;
                if (res.Disposed) continue;
                int idx = (kv.Key.Tag as LyricDisplayItem)?.LineIndex ?? -1;
                float target = (_manualBrowsing || idx == _currentLineIndex || idx == _hoveredIndex)
                    ? 0f : (float)_cachedLyricsBlurAmount;
                AnimateBlurAmount(res, target);
            }
        }

        private void OnScrollPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            SetHoveredLine(HitTestLineIndex(e));
        }

        private void OnScrollPointerExited(object sender, PointerRoutedEventArgs e)
        {
            SetHoveredLine(-1);
        }

        private void SetHoveredLine(int idx)
        {
            if (idx == _currentLineIndex) idx = -1;
            if (idx == _hoveredIndex) return;

            int prev = _hoveredIndex;
            _hoveredIndex = idx;
            if (prev >= 0) ApplyEmphasis(prev, animate: true);
            if (idx >= 0) ApplyEmphasis(idx, animate: true);
        }

        private int HitTestLineIndex(PointerRoutedEventArgs e)
        {
            // 零分配：从 OriginalSource 沿视觉树上溯到带 Tag 的 Border，
            // 避免 GetCurrentPoint 的 PointerPoint 分配与 FindElementsInHostCoordinates 的枚举分配。
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node is not null)
            {
                if (node is Border b && b.Tag is LyricDisplayItem item)
                    return item.LineIndex;
                node = VisualTreeHelper.GetParent(node);
            }
            return -1;
        }

        private void EnsureBlurFactory()
        {
            if (_blurFactory != null) return;
            var compositor = ElementCompositionPreview.GetElementVisual(this)?.Compositor;
            if (compositor == null) return;

            using var effect = new GaussianBlurEffect
            {
                Name = "blur",
                BlurAmount = 0f,
                BorderMode = EffectBorderMode.Soft,
                Optimization = EffectOptimization.Speed,
                Source = new CompositionEffectSourceParameter("src"),
            };
            _blurFactory = compositor.CreateEffectFactory(effect, new[] { "blur.BlurAmount" });
        }

        private void BuildBlurForContainer(Border border, StackPanel panel, bool isCurrent)
        {
            EnsureBlurFactory();
            if (_blurFactory == null) return;

            TeardownBlurForBorder(border);

            var compositor = ElementCompositionPreview.GetElementVisual(border).Compositor;
            var panelVisual = ElementCompositionPreview.GetElementVisual(panel);
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);

            var surface = compositor.CreateVisualSurface();
            surface.SourceVisual = panelVisual;

            var surfaceBrush = compositor.CreateSurfaceBrush(surface);
            surfaceBrush.Stretch = CompositionStretch.None;
            surfaceBrush.HorizontalAlignmentRatio = 0f;
            surfaceBrush.VerticalAlignmentRatio = 0f;

            var effectBrush = _blurFactory.CreateBrush();
            effectBrush.SetSourceParameter("src", surfaceBrush);

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = effectBrush;

            var res = new BlurResources
            {
                Border = border,
                Panel = panel,
                PanelVisual = panelVisual,
                Surface = surface,
                SurfaceBrush = surfaceBrush,
                EffectBrush = effectBrush,
                Sprite = sprite,
            };
            panel.Tag = res;
            panel.SizeChanged -= _onBlurPanelSizeChanged;
            panel.SizeChanged += _onBlurPanelSizeChanged;
            _blurMap[border] = res;

            UpdateBlurGeometry(res);
            ApplyBlurState(res, isCurrent, animate: false);

            if (!_blurGeometryRefreshQueued)
            {
                _blurGeometryRefreshQueued = true;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _refreshBlurGeometryAction);
            }
        }

        private void OnBlurPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is BlurResources res && !res.Disposed)
                UpdateBlurGeometry(res);
        }

        private void RefreshBlurGeometryAll()
        {
            _blurGeometryRefreshQueued = false;
            foreach (var res in _blurMap.Values)
            {
                if (!res.Disposed) UpdateBlurGeometry(res);
            }
        }

        private void UpdateBlurGeometry(BlurResources res)
        {
            var sz = res.Panel.ActualSize;
            float m = (float)Math.Min(Math.Ceiling(_cachedLyricsBlurAmount * 3.0), 10.0);
            res.Surface.SourceOffset = new Vector2(-m, -m);
            res.Surface.SourceSize = new Vector2(sz.X + 2f * m, sz.Y + 2f * m);
            res.Sprite.Size = res.Surface.SourceSize;
            res.Sprite.Offset = new Vector3((float)res.Border.Padding.Left - m, (float)res.Border.Padding.Top - m, 0f);
        }

        private void ApplyBlurState(BlurResources res, bool isCurrent, bool animate)
        {
            if (res.Disposed) return;

            // Option A: the sprite is the permanent renderer and the real panel stays
            // off-screen for every line. Switching current/non-current only animates the
            // blur amount (current = 0, non-current = N), so there is never a swap between
            // the XAML panel and the composition sprite -> no 1-frame seam / translation flash.
            if (!res.Attached)
            {
                ElementCompositionPreview.SetElementChildVisual(res.Border, res.Sprite);
                res.Attached = true;
            }
            res.PanelVisual.Properties.InsertVector3("Translation", new Vector3(BlurOffscreenX, 0f, 0f));

            float target = isCurrent ? 0f : (float)_cachedLyricsBlurAmount;
            if (animate)
                AnimateBlurAmount(res, target);
            else
                SetBlurAmountInstant(res, target);
        }

        private static void SetBlurAmountInstant(BlurResources res, float v)
        {
            res.EffectBrush.Properties.InsertScalar("blur.BlurAmount", v);
        }

        private void AnimateBlurAmount(BlurResources res, float to)
        {
            var compositor = res.Sprite.Compositor;
            _reusableBlurAnim ??= compositor.CreateScalarKeyFrameAnimation();
            _reusableBlurAnim.InsertKeyFrame(1f, to);
            _reusableBlurAnim.Duration = TimeSpan.FromMilliseconds(BlurTransitionDurationMs);
            res.EffectBrush.StartAnimation("blur.BlurAmount", _reusableBlurAnim);
        }

        private void RefreshAllBlur()
        {
            if (_cachedLyricsBlurAmount > 0)
            {
                foreach (var kv in _itemMap)
                {
                    var border = kv.Value.Border;
                    if (border.Child is StackPanel panel)
                        BuildBlurForContainer(border, panel, kv.Key.IsCurrent);
                }
                if (_hoveredIndex >= 0)
                    ApplyEmphasis(_hoveredIndex, animate: false);
            }
            else
            {
                TeardownAllBlur();
            }
        }

        private void TeardownBlurForBorder(Border border)
        {
            if (!_blurMap.TryGetValue(border, out var res)) return;
            _blurMap.Remove(border);
            DisposeBlur(res);
        }

        private void TeardownAllBlur()
        {
            foreach (var res in _blurMap.Values)
                DisposeBlur(res);
            _blurMap.Clear();
        }

        private void DisposeBlur(BlurResources res)
        {
            res.Disposed = true;
            res.Panel.SizeChanged -= _onBlurPanelSizeChanged;
            res.Panel.Tag = null;
            ElementCompositionPreview.SetElementChildVisual(res.Border, null);
            res.PanelVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            res.Sprite.Dispose();
            res.EffectBrush.Dispose();
            res.SurfaceBrush.Dispose();
            res.Surface.Dispose();
        }

        private sealed class BlurResources
        {
            public Border Border = null!;
            public StackPanel Panel = null!;
            public Visual PanelVisual = null!;
            public CompositionVisualSurface Surface = null!;
            public CompositionSurfaceBrush SurfaceBrush = null!;
            public CompositionEffectBrush EffectBrush = null!;
            public SpriteVisual Sprite = null!;
            public bool Attached;
            public bool Disposed;
        }
    }
}
