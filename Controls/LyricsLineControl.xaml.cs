using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Text;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class LyricsLineControl : UserControl
    {
        private CompositionScopedBatch _currentCompositionBatch;
        private InsetClip _currentCompositionClip;
        private TextBlock _currentAnimatingTextBlock;

        public LyricsLineControl()
        {
            this.InitializeComponent();
            this.Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrentAnimation();
        }

        // LyricsText 依赖属性
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

        // TranslateText 依赖属性
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

        // TranslateVisibility 依赖属性
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

        // LineAnimateDuration 依赖属性
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

        // IsCurrentLine 依赖属性 - 核心属性
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

            // 触发 IsCurrentLineEvent 事件
            control.IsCurrentLineEvent?.Invoke(control, new RoutedEventArgs());

            // 控制动画
            if (isCurrentLine)
            {
                // 开始动画
                control.StartTimerAnimation(control.LyricsTextBlock, control.LineAnimateDuration);
            }
            else
            {
                // 结束动画
                control.CancelCurrentAnimation();
            }
        }

        // IsCurrentLineEvent 路由事件
        public event RoutedEventHandler IsCurrentLineEvent;

        // FontFamily 依赖属性
        public new static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(
                nameof(FontFamily),
                typeof(Microsoft.UI.Xaml.Media.FontFamily),
                typeof(LyricsLineControl),
                new PropertyMetadata(new FontFamily("Segoe UI")));

        public new Microsoft.UI.Xaml.Media.FontFamily FontFamily
        {
            get => (Microsoft.UI.Xaml.Media.FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        // LyricsFontSize 依赖属性 - 歌词字体大小
        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(
                nameof(LyricsFontSize),
                typeof(double),
                typeof(LyricsLineControl),
                new PropertyMetadata(32.0));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        // TranslateFontSize 依赖属性 - 翻译字体大小
        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(
                nameof(TranslateFontSize),
                typeof(double),
                typeof(LyricsLineControl),
                new PropertyMetadata(24.0));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        // TextAlignment 依赖属性
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

        // HorizontalAlignment 依赖属性 (覆盖基类)
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

        // IsWFWLyrics 依赖属性 - 控制是否启用逐字动画
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

        #region 动画方法

        private void StartTimerAnimation(TextBlock textBlock, TimeSpan duration)
        {
            CancelCurrentAnimation();

            if (!IsWFWLyrics) return;

            var targetWidth = (float)textBlock.ActualWidth;
            if (targetWidth <= 0)
            {
                return;
            }

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

        // 取消当前动画
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
