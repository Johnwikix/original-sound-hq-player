using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using Windows.UI.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class LyricsLineControl : UserControl
    {
        private CompositionScopedBatch _currentCompositionBatch;
        private InsetClip _currentCompositionClip;
        private TextBlock _currentAnimatingTextBlock;
        private bool _isUpdatingFontSize = false; // ⭐ 防止循环更新的标志

        public LyricsLineControl()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 订阅窗口大小变化
            if (App.MainWindow != null)
            {
                App.MainWindow.SizeChanged += OnWindowSizeChanged;
            }

            // 控件加载时立即更新一次字体大小
            UpdateDynamicFontSizes();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrentAnimation();

            // 取消订阅
            if (App.MainWindow != null)
            {
                App.MainWindow.SizeChanged -= OnWindowSizeChanged;
            }
        }

        private void OnWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
        {
            UpdateDynamicFontSizes();
        }

        // IsGlobalFontSizeEnabled 依赖属性
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
            var control = d as LyricsLineControl;
            control?.UpdateDynamicFontSizes();
        }

        // ⭐ 修改 LyricsFontSize 依赖属性 - 监听外部赋值
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

            // ⭐ 当外部修改字体大小时，如果不是全局字体模式，立即用动态字体覆盖
            if (!control.IsGlobalFontSizeEnabled)
            {
                // 使用 Dispatcher 延迟执行，确保所有绑定都已完成
                control.UpdateDynamicFontSizes();
            }
        }

        // ⭐ TranslateFontSize 依赖属性 - 同样监听外部赋值
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

            // ⭐ 当外部修改字体大小时，如果不是全局字体模式，立即用动态字体覆盖
            if (!control.IsGlobalFontSizeEnabled)
            {
                control.UpdateDynamicFontSizes();
            }
        }

        // ⭐ 核心方法:更新动态字体大小
        private void UpdateDynamicFontSizes()
        {
            // 如果启用了全局字体，不做任何处理
            if (IsGlobalFontSizeEnabled)
            {
                return;
            }

            // 检查窗口是否可用
            if (App.MainWindow?.AppWindow?.Size.Width is null || App.MainWindow.AppWindow.Size.Width == 0)
            {
                return;
            }

            var width = App.MainWindow.AppWindow.Size.Width;
            var scaledWidth = width / AppData.AppDpiScale;

            // ⭐ 设置标志，防止触发属性变化回调
            _isUpdatingFontSize = true;

            try
            {
                // 更新歌词字体大小
                LyricsFontSize = CalculateFontSize(scaledWidth, true);

                // 更新翻译字体大小
                TranslateFontSize = CalculateFontSize(scaledWidth, false);
            }
            finally
            {
                // ⭐ 恢复标志
                _isUpdatingFontSize = false;
            }
        }

        // 计算字体大小
        private double CalculateFontSize(double scaledWidth, bool isLyricsType)
        {
            if (scaledWidth <= 1440)
                return isLyricsType ? 28.0 : 22.0;
            if (scaledWidth <= 1680)
                return isLyricsType ? 30.0 : 24.0;
            if (scaledWidth <= 1920)
                return isLyricsType ? 32.0 : 26.0;
            if (scaledWidth <= 2160)
                return isLyricsType ? 36.0 : 30.0;
            if (scaledWidth <= 2560)
                return isLyricsType ? 40.0 : 34.0;

            return isLyricsType ? 44.0 : 38.0;
        }

        #region 其他依赖属性

        public static readonly DependencyProperty LyricsTextProperty =
            DependencyProperty.Register(
                nameof(LyricsText),
                typeof(string),
                typeof(LyricsLineControl),
                new PropertyMetadata(string.Empty));

        public string LyricsText
        {
            get => (string)GetValue(LyricsTextProperty);
            set => SetValue(LyricsTextProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(
                nameof(TranslateText),
                typeof(string),
                typeof(LyricsLineControl),
                new PropertyMetadata(string.Empty));

        public string TranslateText
        {
            get => (string)GetValue(TranslateTextProperty);
            set => SetValue(TranslateTextProperty, value);
        }

        public static readonly DependencyProperty TranslateVisibilityProperty =
            DependencyProperty.Register(
                nameof(TranslateVisibility),
                typeof(Visibility),
                typeof(LyricsLineControl),
                new PropertyMetadata(Visibility.Collapsed));

        public Visibility TranslateVisibility
        {
            get => (Visibility)GetValue(TranslateVisibilityProperty);
            set => SetValue(TranslateVisibilityProperty, value);
        }

        public static readonly DependencyProperty LineAnimateDurationProperty =
            DependencyProperty.Register(
                nameof(LineAnimateDuration),
                typeof(TimeSpan),
                typeof(LyricsLineControl),
                new PropertyMetadata(TimeSpan.Zero));

        public TimeSpan LineAnimateDuration
        {
            get => (TimeSpan)GetValue(LineAnimateDurationProperty);
            set => SetValue(LineAnimateDurationProperty, value);
        }

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
            var control = d as LyricsLineControl;
            if (control == null) return;

            bool isCurrentLine = (bool)e.NewValue;

            if (isCurrentLine)
            {
                control.StartTimerAnimation(control.LyricsTextBlock, control.LineAnimateDuration);
                control.IsCurrentLineEvent?.Invoke(control, new RoutedEventArgs());
            }
            else
            {
                control.CancelCurrentAnimation();
            }
        }

        public event RoutedEventHandler IsCurrentLineEvent;

        public new static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(
                nameof(FontFamily),
                typeof(Microsoft.UI.Xaml.Media.FontFamily),
                typeof(LyricsLineControl),
                new PropertyMetadata(new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI")));

        public new Microsoft.UI.Xaml.Media.FontFamily FontFamily
        {
            get => (Microsoft.UI.Xaml.Media.FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(
                nameof(TextAlignment),
                typeof(TextAlignment),
                typeof(LyricsLineControl),
                new PropertyMetadata(TextAlignment.Left));

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public new static readonly DependencyProperty HorizontalAlignmentProperty =
            DependencyProperty.Register(
                nameof(HorizontalAlignment),
                typeof(HorizontalAlignment),
                typeof(LyricsLineControl),
                new PropertyMetadata(HorizontalAlignment.Left));

        public new HorizontalAlignment HorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(HorizontalAlignmentProperty);
            set => SetValue(HorizontalAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsWFWLyricsProperty =
            DependencyProperty.Register(
                nameof(IsWFWLyrics),
                typeof(bool),
                typeof(LyricsLineControl),
                new PropertyMetadata(true));

        public bool IsWFWLyrics
        {
            get => (bool)GetValue(IsWFWLyricsProperty);
            set => SetValue(IsWFWLyricsProperty, value);
        }

        #endregion

        #region 动画方法

        private void StartTimerAnimation(TextBlock textBlock, TimeSpan duration)
        {
            CancelCurrentAnimation();

            if (!IsWFWLyrics) return;

            // ⭐ 校验 duration 范围，WinUI Composition 要求 >= 1ms 且 <= 24天
            var minDuration = TimeSpan.FromMilliseconds(1);
            var maxDuration = TimeSpan.FromDays(24);
            if (duration < minDuration || duration > maxDuration)
            {
                duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 1, maxDuration.TotalMilliseconds));
                // 如果原始值是 0 或负数，直接跳过动画
                if (duration.TotalMilliseconds < 1) return;
            }

            var targetWidth = (float)textBlock.ActualWidth;
            if (targetWidth <= 0) return;

            var visual = ElementCompositionPreview.GetElementVisual(textBlock);
            var compositor = visual.Compositor;

            var clip = compositor.CreateInsetClip();
            clip.LeftInset = 0;
            clip.TopInset = 0;
            clip.BottomInset = 0;
            clip.RightInset = targetWidth;

            visual.Clip = clip;

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = duration;
            animation.InsertKeyFrame(0.0f, targetWidth);
            animation.InsertKeyFrame(1.0f, 0.0f, compositor.CreateLinearEasingFunction());

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _currentAnimatingTextBlock = textBlock;
            _currentCompositionBatch = batch;
            _currentCompositionClip = clip;

            batch.Completed += OnCompositionBatchCompleted;
            clip.StartAnimation("RightInset", animation);
            batch.End();
        }

        private void OnCompositionBatchCompleted(object sender, CompositionBatchCompletedEventArgs args)
        {
            var batch = (CompositionScopedBatch)sender;
            batch.Completed -= OnCompositionBatchCompleted;

            _currentCompositionBatch = null;
            _currentCompositionClip = null;

            if (_currentAnimatingTextBlock != null)
            {
                var visual = ElementCompositionPreview.GetElementVisual(_currentAnimatingTextBlock);
                visual.Clip = null;
                _currentAnimatingTextBlock = null;
            }
        }

        public void CancelCurrentAnimation()
        {
            if (_currentCompositionBatch != null)
            {
                _currentCompositionBatch.Completed -= OnCompositionBatchCompleted;
                _currentCompositionBatch = null;
            }

            if (_currentCompositionClip != null)
            {
                _currentCompositionClip.StopAnimation("RightInset");
                _currentCompositionClip = null;
            }

            if (_currentAnimatingTextBlock != null)
            {
                var visual = ElementCompositionPreview.GetElementVisual(_currentAnimatingTextBlock);
                visual.Clip = null;
                _currentAnimatingTextBlock = null;
            }
        }

        #endregion
    }
}