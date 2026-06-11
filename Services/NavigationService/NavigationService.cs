using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Concurrent;
using System.Threading;
using Windows.Foundation;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public class NavigationService : INavigationService
    {
        private readonly ConcurrentDictionary<Type, Type> _registeredPages = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly EasingFunctionBase _easingOutFunction;

        // volatile 保证多线程可见性；Show/Dismiss 用 Interlocked.CompareExchange 做互斥
        private volatile int _isAnimating = 0; // 0 = idle, 1 = busy
        private ILogger<NavigationService> _logger;

        public Frame ContentFrame { get; set; }
        public bool CanGoBack => ContentFrame?.CanGoBack ?? false;

        public NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _easingOutFunction = new ExponentialEase
            {
                EasingMode = EasingMode.EaseOut,
                Exponent = 8
            };
        }

        // ────────────────────────────────────────────────────────────
        //  初始化 / 注册
        // ────────────────────────────────────────────────────────────

        public void Initialize(Frame frame) => ContentFrame = frame;

        public void RegisterPage<T>() where T : Page =>
            _registeredPages[typeof(T)] = typeof(T);

        // ────────────────────────────────────────────────────────────
        //  Navigate（Frame 级切页）
        // ────────────────────────────────────────────────────────────

        public void Navigate(
            Type pageType,
            object parameter = null,
            NavigationTransitionInfo transitionInfo = null,
            int animeTime = 300,
            bool isPlayAnime = false)
        {
            if (ContentFrame?.Content?.GetType() == pageType)
            {
                if (ContentFrame.Content is INavigatable nav && parameter is not null)
                    nav.ReceiveNavigationParameter(parameter);
                if (!isPlayAnime) return;
            }

            if (_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;
                transitionInfo ??= new EntranceNavigationTransitionInfo();
                AnimatePageTransition(pageInstance, transitionInfo, animeTime);

                if (pageInstance is INavigatable nav)
                    nav.ReceiveNavigationParameter(parameter);
            }
            else
            {
                ContentFrame?.Navigate(pageType, parameter, transitionInfo);
            }
        }

        private void AnimatePageTransition(Page newPage, NavigationTransitionInfo transitionInfo, int animeTime)
        {
            try
            {
                // 清理旧页面残留变换
                if (ContentFrame.Content is FrameworkElement current)
                {
                    current.RenderTransform = null;
                    current.ClearValue(UIElement.RenderTransformProperty);
                    current.Opacity = 1;
                }

                ContentFrame.Content = newPage;
                ContentFrame.Visibility = Visibility.Visible;
                ContentFrame.Opacity = 1;

                if (transitionInfo is SlideNavigationTransitionInfo slide)
                    ExecuteSlideAnimation(newPage, slide.Effect, animeTime);
                else if (transitionInfo is DrillInNavigationTransitionInfo)
                    ExecuteDrillInAnimation(newPage, animeTime);
                else if (transitionInfo is EntranceNavigationTransitionInfo)
                    ExecuteEntranceAnimation(newPage, animeTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AnimatePageTransition 动画错误: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  具体动画实现（均读 ContentFrame 尺寸，保持一致）
        // ────────────────────────────────────────────────────────────

        private void ExecuteSlideAnimation(Page page, SlideNavigationTransitionEffect effect, int animeTime)
        {
            var translateTransform = new TranslateTransform();
            page.RenderTransform = translateTransform;

            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingOutFunction
            };

            switch (effect)
            {
                case SlideNavigationTransitionEffect.FromRight:
                    animation.From = ContentFrame.ActualWidth;
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "X");
                    break;
                case SlideNavigationTransitionEffect.FromLeft:
                    animation.From = -ContentFrame.ActualWidth;
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "X");
                    break;
                case SlideNavigationTransitionEffect.FromBottom:
                    // 修复：用 SafeHeight 而非直接读 ActualHeight
                    animation.From = SafeHeight(ContentFrame);
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "Y");
                    break;
                default:
                    page.RenderTransform = null;
                    page.ClearValue(UIElement.RenderTransformProperty);
                    return;
            }

            var sb = new Storyboard();
            sb.Children.Add(animation);

            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                sb.Completed -= onCompleted;
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
            };
            sb.Completed += onCompleted;
            sb.Begin();
        }

        private void ExecuteDrillInAnimation(Page page, int animeTime)
        {
            var composite = new CompositeTransform { ScaleX = 1.1, ScaleY = 1.1 };
            page.RenderTransformOrigin = new Point(0.5, 0.5);
            page.RenderTransform = composite;
            page.Opacity = 0;

            var scaleX = new DoubleAnimation { From = 1.1, To = 1.0, Duration = TimeSpan.FromMilliseconds(animeTime), EasingFunction = _easingOutFunction };
            var scaleY = new DoubleAnimation { From = 1.1, To = 1.0, Duration = TimeSpan.FromMilliseconds(animeTime), EasingFunction = _easingOutFunction };
            var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(animeTime) };

            Storyboard.SetTarget(scaleX, composite); Storyboard.SetTargetProperty(scaleX, "ScaleX");
            Storyboard.SetTarget(scaleY, composite); Storyboard.SetTargetProperty(scaleY, "ScaleY");
            Storyboard.SetTarget(opacity, page); Storyboard.SetTargetProperty(opacity, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Children.Add(opacity);

            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                sb.Completed -= onCompleted;
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
                page.RenderTransformOrigin = new Point(0, 0);
                page.Opacity = 1;
            };
            sb.Completed += onCompleted;
            sb.Begin();
        }

        private void ExecuteEntranceAnimation(Page page, int animeTime)
        {
            var translate = new TranslateTransform();
            page.RenderTransform = translate;
            page.Opacity = 0;

            var translateAnim = new DoubleAnimation { From = 500, To = 0, Duration = TimeSpan.FromMilliseconds(animeTime), EasingFunction = _easingOutFunction };
            var opacityAnim = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(animeTime) };

            Storyboard.SetTarget(translateAnim, translate); Storyboard.SetTargetProperty(translateAnim, "Y");
            Storyboard.SetTarget(opacityAnim, page); Storyboard.SetTargetProperty(opacityAnim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(translateAnim);
            sb.Children.Add(opacityAnim);

            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                sb.Completed -= onCompleted;
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
                page.Opacity = 1;
            };
            sb.Completed += onCompleted;
            sb.Begin();
        }

        // ────────────────────────────────────────────────────────────
        //  FadeShow / FadeDismiss（不参与 _isAnimating 互斥）
        // ────────────────────────────────────────────────────────────

        public void FadeShow(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null) return;
            ContentFrame.Visibility = Visibility.Visible;

            var opacityAnim = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(animeTime) };
            Storyboard.SetTarget(opacityAnim, ContentFrame);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(opacityAnim);

            EventHandler<object> onAnimCompleted = null;
            onAnimCompleted = (s, e) =>
            {
                sb.Completed -= onAnimCompleted;
                // 修复：FadeShow 不应操作 _isAnimating，去掉原来的误写
                onCompleted?.Invoke();
            };
            sb.Completed += onAnimCompleted;
            sb.Begin();
        }

        public void FadeDismiss(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null || ContentFrame.Visibility == Visibility.Collapsed) return;

            var opacityAnim = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(animeTime) };
            Storyboard.SetTarget(opacityAnim, ContentFrame);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(opacityAnim);

            EventHandler<object> onAnimCompleted = null;
            onAnimCompleted = (s, e) =>
            {
                sb.Completed -= onAnimCompleted;
                ContentFrame.Visibility = Visibility.Collapsed;
                ContentFrame.Opacity = 1;
                // 修复：FadeDismiss 不应操作 _isAnimating，去掉原来的误写
                onCompleted?.Invoke();
            };
            sb.Completed += onAnimCompleted;
            sb.Begin();
        }

        // ────────────────────────────────────────────────────────────
        //  Show（overlay 滑入，带布局等待保护）
        // ────────────────────────────────────────────────────────────

        public void Show(Type pageType, int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null) return;
            // 原子性抢锁：只有从 0 → 1 成功才继续
            if (Interlocked.CompareExchange(ref _isAnimating, 1, 0) != 0) return;

            if (!_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                Interlocked.Exchange(ref _isAnimating, 0);
                return;
            }

            try
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;
                ContentFrame.Content = pageInstance;
                ContentFrame.Visibility = Visibility.Visible;
                if (pageInstance is PlayingDetailPage playingPage)
                    playingPage.ResumeBackgroundRendering();

                void StartAnimation()
                {
                    var height = SafeHeight(ContentFrame);

                    var translate = new TranslateTransform();
                    ContentFrame.RenderTransform = translate;

                    var slideAnim = new DoubleAnimation
                    {
                        From = height,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(animeTime),
                        EasingFunction = _easingOutFunction
                    };
                    var opacityAnim = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(animeTime) };

                    Storyboard.SetTarget(slideAnim, translate); Storyboard.SetTargetProperty(slideAnim, "Y");
                    Storyboard.SetTarget(opacityAnim, ContentFrame); Storyboard.SetTargetProperty(opacityAnim, "Opacity");

                    var sb = new Storyboard();
                    sb.Children.Add(slideAnim);
                    sb.Children.Add(opacityAnim);

                    EventHandler<object> onAnimCompleted = null;
                    onAnimCompleted = (s, e) =>
                    {
                        sb.Completed -= onAnimCompleted;
                        ContentFrame.RenderTransform = null;
                        ContentFrame.ClearValue(UIElement.RenderTransformProperty);
                        Interlocked.Exchange(ref _isAnimating, 0);
                        onCompleted?.Invoke();
                    };
                    sb.Completed += onAnimCompleted;
                    sb.Begin();
                }

                if (ContentFrame.ActualHeight > 0)
                {
                    StartAnimation();
                }
                else
                {
                    // 窗口刚从托盘恢复，布局尚未完成，等 LayoutUpdated 后再启动
                    void OnLayoutUpdated(object? s, object e)
                    {
                        if (ContentFrame.ActualHeight <= 0) return;
                        ContentFrame.LayoutUpdated -= OnLayoutUpdated;
                        StartAnimation();
                    }
                    ContentFrame.LayoutUpdated += OnLayoutUpdated;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Show 滑入错误: {ex.Message}");
                Interlocked.Exchange(ref _isAnimating, 0);
            }
            finally {
                _ = WorkingSetCompressor.TrimSelfAsync();
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Dismiss（overlay 滑出）
        // ────────────────────────────────────────────────────────────

        public void Dismiss(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null || ContentFrame.Visibility == Visibility.Collapsed) return;
            if (Interlocked.CompareExchange(ref _isAnimating, 1, 0) != 0) return;

            try
            {
                var height = SafeHeight(ContentFrame);

                var translate = new TranslateTransform();
                ContentFrame.RenderTransform = translate;

                var slideAnim = new DoubleAnimation { From = 0, To = height, Duration = TimeSpan.FromMilliseconds(animeTime), EasingFunction = _easingOutFunction };
                var opacityAnim = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(animeTime) };

                Storyboard.SetTarget(slideAnim, translate); Storyboard.SetTargetProperty(slideAnim, "Y");
                Storyboard.SetTarget(opacityAnim, ContentFrame); Storyboard.SetTargetProperty(opacityAnim, "Opacity");

                var sb = new Storyboard();
                sb.Children.Add(slideAnim);
                sb.Children.Add(opacityAnim);

                EventHandler<object> onAnimCompleted = null;
                onAnimCompleted = (s, e) =>
                {
                    sb.Completed -= onAnimCompleted;
                    ContentFrame.Visibility = Visibility.Collapsed;
                    if (ContentFrame.Content is PlayingDetailPage playingPage)
                        playingPage.PauseBackgroundRendering();
                    ContentFrame.Opacity = 1;
                    ContentFrame.RenderTransform = null;
                    ContentFrame.ClearValue(UIElement.RenderTransformProperty);
                    Interlocked.Exchange(ref _isAnimating, 0);
                    onCompleted?.Invoke();
                };
                sb.Completed += onAnimCompleted;
                sb.Begin();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Dismiss 滑出错误: {ex.Message}");
                Interlocked.Exchange(ref _isAnimating, 0);
            }
            finally {
                _ = WorkingSetCompressor.TrimSelfAsync();
            }
        }

        // ────────────────────────────────────────────────────────────
        //  GoBack
        // ────────────────────────────────────────────────────────────

        public void GoBack() => ContentFrame?.GoBack();

        // ────────────────────────────────────────────────────────────
        //  工具方法
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 读取一个稳定的高度用于滑动动画的起止值。
        ///
        /// 关键原则：ContentFrame 自身在 Visibility 切换时会触发 Measure/Arrange，
        /// 其 ActualHeight 在那一帧内是不稳定的，即使值看起来非零也可能在动画过程中
        /// 被布局引擎重新约束，导致最大化窗口被压缩。
        /// 因此优先向上遍历父容器，父容器不参与本次 Visibility 变更，布局状态稳定。
        /// </summary>
        private static double SafeHeight(FrameworkElement element)
        {
            // 向上最多走 4 层，找第一个高度稳定的祖先
            DependencyObject current = element.Parent;
            for (int i = 0; i < 4; i++)
            {
                if (current is FrameworkElement fe && fe.ActualHeight > 0)
                    return fe.ActualHeight;
                if (current is null) break;
                current = (current as FrameworkElement)?.Parent;
            }
            // 父容器也还没布局完成（极端情况），退回自身
            if (element.ActualHeight > 0) return element.ActualHeight;
            return 800;
        }
    }
}