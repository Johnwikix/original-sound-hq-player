using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsLineControl : UserControl
    {
        // ── 逐字动画状态 ─────────────────────────────────────────────
        private readonly List<(TextBlock tb, InsetClip clip)> _wordClips = [];
        private bool _clipsInitialized = false;
        private bool _isUpdatingFontSize = false;

        public LyricsLineControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow != null)
                App.MainWindow.SizeChanged += OnWindowSizeChanged;

            UpdateDynamicFontSizes();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrentAnimation();

            if (App.MainWindow != null)
                App.MainWindow.SizeChanged -= OnWindowSizeChanged;
        }

        private void OnWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
        {
            UpdateDynamicFontSizes();
            // 窗口大小变化时，TextBlock 的 ActualWidth 会改变，需要重新初始化 clips
            _clipsInitialized = false;
        }

        // ── IsGlobalFontSizeEnabled ──────────────────────────────────
        public static readonly DependencyProperty IsGlobalFontSizeEnabledProperty =
            DependencyProperty.Register(
                nameof(IsGlobalFontSizeEnabled),
                typeof(bool),
                typeof(LyricsLineControl),
                new PropertyMetadata(false, OnIsGlobalFontSizeEnabledChanged));

        public bool IsGlobalFontSizeEnabled
        {
            get => (bool)GetValue(IsGlobalFontSizeEnabledProperty);
            set => SetValue(IsGlobalFontSizeEnabledProperty, value);
        }

        private static void OnIsGlobalFontSizeEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as LyricsLineControl)?.UpdateDynamicFontSizes();
        }

        // ── LyricsFontSize ───────────────────────────────────────────
        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(
                nameof(LyricsFontSize),
                typeof(double),
                typeof(LyricsLineControl),
                new PropertyMetadata(32.0, OnLyricsFontSizeChanged));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        private static void OnLyricsFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LyricsLineControl;
            if (control == null || control._isUpdatingFontSize) return;
            if (!control.IsGlobalFontSizeEnabled)
                control.UpdateDynamicFontSizes();
        }

        // ── TranslateFontSize ────────────────────────────────────────
        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(
                nameof(TranslateFontSize),
                typeof(double),
                typeof(LyricsLineControl),
                new PropertyMetadata(24.0, OnTranslateFontSizeChanged));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        private static void OnTranslateFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LyricsLineControl;
            if (control == null || control._isUpdatingFontSize) return;
            if (!control.IsGlobalFontSizeEnabled)
                control.UpdateDynamicFontSizes();
        }

        // ── CurrentPlayingTime（由 LyricsControl 内部 Timer 驱动）──────
        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(
                nameof(CurrentPlayingTime),
                typeof(TimeSpan),
                typeof(LyricsLineControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LyricsLineControl ctrl && ctrl.IsCurrentLine && ctrl.IsWFWLyrics)
                ctrl.UpdateWordProgress((TimeSpan)e.NewValue);
        }

        // ── 字体大小计算 ─────────────────────────────────────────────
        private void UpdateDynamicFontSizes()
        {
            if (IsGlobalFontSizeEnabled) return;

            if (App.MainWindow?.AppWindow?.Size.Width is null || App.MainWindow.AppWindow.Size.Width == 0)
                return;

            var width = App.MainWindow.AppWindow.Size.Width;
            var scaledWidth = width / AppData.AppDpiScale;

            _isUpdatingFontSize = true;
            try
            {
                LyricsFontSize = CalculateFontSize(scaledWidth, true);
                TranslateFontSize = CalculateFontSize(scaledWidth, false);
            }
            finally
            {
                _isUpdatingFontSize = false;
            }
        }

        private static double CalculateFontSize(double scaledWidth, bool isLyricsType)
        {
            if (scaledWidth <= 1440) return isLyricsType ? 32.0 : 24.0;
            if (scaledWidth <= 1680) return isLyricsType ? 34.0 : 26.0;
            if (scaledWidth <= 1920) return isLyricsType ? 36.0 : 28.0;
            if (scaledWidth <= 2160) return isLyricsType ? 38.0 : 30.0;
            if (scaledWidth <= 2560) return isLyricsType ? 42.0 : 34.0;
            return isLyricsType ? 46.0 : 38.0;
        }

        // ── 逐字动画核心 ─────────────────────────────────────────────

        /// <summary>
        /// 懒初始化：为每个词的 TextBlock 创建 InsetClip，初始完全遮住（RightInset = ActualWidth）。
        /// 只有在 IsCurrentLine=true 后第一次 CurrentPlayingTime 到来时才执行。
        /// </summary>
        private void EnsureWordClipsInitialized()
        {
            if (_clipsInitialized) return;
            _wordClips.Clear();

            for (int i = 0; i < LyricsItemsControl.Items.Count; i++)
            {
                var container = LyricsItemsControl.ContainerFromIndex(i) as ContentPresenter;
                if (container == null) continue;

                var tb = VisualTreeHelper.GetChild(container, 0) as TextBlock;
                if (tb == null) continue;

                var visual = ElementCompositionPreview.GetElementVisual(tb);
                var clip = visual.Compositor.CreateInsetClip();

                // 初始状态：根据当前时间决定是否已经播完
                clip.RightInset = (float)tb.ActualWidth;
                visual.Clip = clip;

                _wordClips.Add((tb, clip));
            }

            _clipsInitialized = true;
        }

        /// <summary>
        /// 每帧（~16.67ms）由 CurrentPlayingTime 驱动，直接写 RightInset，不使用 Composition 动画对象。
        /// 暂停时 Timer 停止 → CurrentPlayingTime 不变 → RightInset 冻结，完全支持暂停。
        /// </summary>
        private void UpdateWordProgress(TimeSpan currentTime)
        {
            if (DataContext is not LyricLine currentLine) return;

            EnsureWordClipsInitialized();

            var words = currentLine.Words;
            int count = Math.Min(words.Count, _wordClips.Count);

            for (int i = 0; i < count; i++)
            {
                var word = words[i];
                var (tb, clip) = _wordClips[i];

                var elapsed = currentTime - word.StartTime;

                float rightInset;
                if (elapsed <= TimeSpan.Zero)
                {
                    // 还没到这个词
                    rightInset = (float)tb.ActualWidth;
                }
                else if (word.Duration <= TimeSpan.Zero || elapsed >= word.Duration)
                {
                    // 已播完
                    rightInset = 0f;
                }
                else
                {
                    // 线性插值
                    float progress = (float)(elapsed.TotalMilliseconds / word.Duration.TotalMilliseconds);
                    rightInset = (float)tb.ActualWidth * (1f - progress);
                }

                clip.RightInset = rightInset;
            }
        }

        // ── IsCurrentLine ────────────────────────────────────────────
        public static readonly DependencyProperty IsCurrentLineProperty =
            DependencyProperty.Register(
                nameof(IsCurrentLine),
                typeof(bool),
                typeof(LyricsLineControl),
                new PropertyMetadata(false, OnIsCurrentLineChanged));

        public bool IsCurrentLine
        {
            get => (bool)GetValue(IsCurrentLineProperty);
            set => SetValue(IsCurrentLineProperty, value);
        }

        private static void OnIsCurrentLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not LyricsLineControl control) return;

            if ((bool)e.NewValue)
            {
                // 成为当前行：重置 clips 状态，等待 CurrentPlayingTime 驱动
                // 不立即初始化，因为此时 TextBlock 的 ActualWidth 可能还未测量
                control._clipsInitialized = false;
                control.IsCurrentLineEvent?.Invoke(control, new RoutedEventArgs());
            }
            else
            {
                // 离开当前行：全部词显示完整（RightInset = 0），清理 clips
                foreach (var (tb, clip) in control._wordClips)
                {
                    clip.RightInset = 0f;
                    // 移除 clip，恢复默认渲染
                    var visual = ElementCompositionPreview.GetElementVisual(tb);
                    visual.Clip = null;
                }
                control._wordClips.Clear();
                control._clipsInitialized = false;
            }
        }

        public event RoutedEventHandler IsCurrentLineEvent;

        // ── CancelCurrentAnimation ───────────────────────────────────
        public void CancelCurrentAnimation()
        {
            foreach (var (tb, _) in _wordClips)
            {
                var visual = ElementCompositionPreview.GetElementVisual(tb);
                visual.Clip = null;
            }
            _wordClips.Clear();
            _clipsInitialized = false;
        }

        // ── 其他依赖属性 ─────────────────────────────────────────────

        public static readonly DependencyProperty LyricWordsProperty =
            DependencyProperty.Register(nameof(LyricWords), typeof(object), typeof(LyricsLineControl),
                new PropertyMetadata(null));

        public object LyricWords
        {
            get => GetValue(LyricWordsProperty);
            set => SetValue(LyricWordsProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(nameof(TranslateText), typeof(string), typeof(LyricsLineControl),
                new PropertyMetadata(string.Empty));

        public string TranslateText
        {
            get => (string)GetValue(TranslateTextProperty);
            set => SetValue(TranslateTextProperty, value);
        }

        public static readonly DependencyProperty TranslateVisibilityProperty =
            DependencyProperty.Register(nameof(TranslateVisibility), typeof(Visibility), typeof(LyricsLineControl),
                new PropertyMetadata(Visibility.Collapsed));

        public Visibility TranslateVisibility
        {
            get => (Visibility)GetValue(TranslateVisibilityProperty);
            set => SetValue(TranslateVisibilityProperty, value);
        }

        public new static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(Microsoft.UI.Xaml.Media.FontFamily),
                typeof(LyricsLineControl),
                new PropertyMetadata(new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI")));

        public new Microsoft.UI.Xaml.Media.FontFamily FontFamily
        {
            get => (Microsoft.UI.Xaml.Media.FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment),
                typeof(LyricsLineControl),
                new PropertyMetadata(TextAlignment.Left));

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public new static readonly DependencyProperty HorizontalAlignmentProperty =
            DependencyProperty.Register(nameof(HorizontalAlignment), typeof(HorizontalAlignment),
                typeof(LyricsLineControl),
                new PropertyMetadata(HorizontalAlignment.Left));

        public new HorizontalAlignment HorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(HorizontalAlignmentProperty);
            set => SetValue(HorizontalAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsWFWLyricsProperty =
            DependencyProperty.Register(nameof(IsWFWLyrics), typeof(bool), typeof(LyricsLineControl),
                new PropertyMetadata(true));

        public bool IsWFWLyrics
        {
            get => (bool)GetValue(IsWFWLyricsProperty);
            set => SetValue(IsWFWLyricsProperty, value);
        }
    }
}