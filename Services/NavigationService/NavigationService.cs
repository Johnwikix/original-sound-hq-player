using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.ViewManagement;
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
        private Action? _cancelPendingHdrEntrance;
        public bool IsPreparingShow => _cancelPendingHdrEntrance is not null;

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

        public void Show(Type pageType, int animeTime = 300, Action? onCompleted = null, Action? onRevealing = null,
            Action? onEntranceStarted = null, Action? onPreparingHdr = null)
        {
            if (ContentFrame is not { } frame || Interlocked.CompareExchange(ref _isAnimating, 1, 0) != 0)
            {
                NotifyCompleted(onCompleted, nameof(Show));
                return;
            }

            if (!_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                Interlocked.Exchange(ref _isAnimating, 0);
                onCompleted?.Invoke();
                return;
            }

            PlayingDetailPage? playingPage = null;
            bool wasHitTestVisible = frame.IsHitTestVisible;
            Action? entranceStarted = onEntranceStarted;
            try
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;
                playingPage = pageInstance as PlayingDetailPage;
                bool prepareHdr = playingPage?.ShouldPrepareHdrEntrance == true;
                if (prepareHdr) NotifyCompleted(onPreparingHdr, nameof(Show));
                if (playingPage is not null)
                {
                    _ = playingPage.SetCanvasNavigationTransitionAsync(!prepareHdr);
                    frame.Opacity = 0;
                    if (prepareHdr)
                        frame.IsHitTestVisible = false;
                }
                frame.Content = pageInstance;
                frame.Visibility = Visibility.Visible;

                if (prepareHdr && playingPage is not null)
                {
                    playingPage.ResumeCanvasRendering(prepareHdr: true);
                    _ = RevealWhenPreparedAsync(playingPage);
                }
                else
                {
                    playingPage?.ResumeCanvasRendering();
                    StartAnimationWhenArranged();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Show 滑入错误");
                onRevealing?.Invoke();
                FinishShow();
            }

            async Task RevealWhenPreparedAsync(PlayingDetailPage page)
            {
                using var preparation = new CancellationTokenSource();
                _cancelPendingHdrEntrance = CancelPreparedShow;
                frame.Unloaded += CancelOnUnload;
                try
                {
                    while (await page.PrepareHdrEntranceAsync(preparation.Token) && page.ShouldPrepareHdrEntrance)
                    {
                        preparation.Token.ThrowIfCancellationRequested();
                        if (!page.IsHdrEntrancePresented) continue;

                        if (animeTime > 0 && new UISettings().AnimationsEnabled)
                        {
                            try
                            {
                                bool completed = await page.RunHdrEntranceMotionAsync(
                                    token => SlideHdrIntoViewAsync(frame, animeTime, NotifyEntranceStarted, token),
                                    () => frame.Opacity = 0, preparation.Token);
                                if (!completed) continue;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "HDR 播放页滑入动画失败");
                                if (!page.IsHdrEntrancePresented) continue;
                            }
                        }

                        preparation.Token.ThrowIfCancellationRequested();
                        NotifyEntranceStarted();
                        _cancelPendingHdrEntrance = null;
                        // Keep the previous page visible beneath the slide until
                        // the opaque HDR frame fills the viewport.
                        onRevealing?.Invoke();
                        FinishShow();
                        return;
                    }

                    preparation.Token.ThrowIfCancellationRequested();
                    await page.PrepareSdrEntranceAsync().WaitAsync(preparation.Token);
                    preparation.Token.ThrowIfCancellationRequested();
                    _cancelPendingHdrEntrance = null;
                    StartAnimationWhenArranged();
                }
                catch (OperationCanceledException)
                {
                    CancelPreparedShow();
                }
                catch (Exception ex)
                {
                    if (_cancelPendingHdrEntrance != (Action)CancelPreparedShow) return;
                    _cancelPendingHdrEntrance = null;
                    _logger.LogError(ex, "Show 播放页呈现错误");
                    _ = page.SetCanvasNavigationTransitionAsync(true);
                    StartAnimationWhenArranged();
                }
                finally
                {
                    frame.Unloaded -= CancelOnUnload;
                }

                void CancelPreparedShow()
                {
                    if (_cancelPendingHdrEntrance != (Action)CancelPreparedShow) return;
                    _cancelPendingHdrEntrance = null;
                    try
                    {
                        preparation.Cancel();
                        frame.Visibility = Visibility.Collapsed;
                        frame.Opacity = 1;
                        frame.IsHitTestVisible = wasHitTestVisible;
                        frame.ClearValue(UIElement.RenderTransformProperty);
                        page.PauseCanvasRendering();
                        _ = page.SetCanvasNavigationTransitionAsync(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "取消播放页入场失败");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isAnimating, 0);
                    }
                    NotifyCompleted(onCompleted, nameof(Show));
                }

                void CancelOnUnload(object sender, RoutedEventArgs args) => CancelPreparedShow();
            }

            void StartAnimationWhenArranged()
            {
                if (frame.ActualHeight > 0)
                {
                    StartAnimation();
                    return;
                }
                frame.LayoutUpdated += OnLayoutUpdated;
                frame.Unloaded += OnUnloaded;

                void Detach()
                {
                    frame.LayoutUpdated -= OnLayoutUpdated;
                    frame.Unloaded -= OnUnloaded;
                }
                void OnLayoutUpdated(object? sender, object args)
                {
                    if (frame.ActualHeight <= 0) return;
                    Detach();
                    StartAnimation();
                }
                void OnUnloaded(object sender, RoutedEventArgs args)
                {
                    Detach();
                    FinishShow();
                }
            }

            void StartAnimation()
            {
                frame.IsHitTestVisible = wasHitTestVisible;
                onRevealing?.Invoke();
                var translate = new TranslateTransform();
                frame.RenderTransform = translate;
                var slide = new DoubleAnimation
                {
                    From = SafeHeight(frame),
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                    EasingFunction = _easingOutFunction
                };
                var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(animeTime) };
                Storyboard.SetTarget(slide, translate); Storyboard.SetTargetProperty(slide, "Y");
                Storyboard.SetTarget(opacity, frame); Storyboard.SetTargetProperty(opacity, "Opacity");
                var storyboard = new Storyboard();
                storyboard.Children.Add(slide);
                storyboard.Children.Add(opacity);
                storyboard.Completed += OnAnimationCompleted;
                storyboard.Begin();
                NotifyEntranceStarted();

                void OnAnimationCompleted(object? sender, object args)
                {
                    storyboard.Completed -= OnAnimationCompleted;
                    storyboard.Stop();
                    FinishShow();
                }
            }

            void NotifyEntranceStarted()
            {
                var callback = entranceStarted;
                entranceStarted = null;
                NotifyCompleted(callback, nameof(Show));
            }

            void FinishShow()
            {
                frame.Opacity = 1;
                frame.IsHitTestVisible = wasHitTestVisible;
                frame.ClearValue(UIElement.RenderTransformProperty);
                _ = playingPage?.SetCanvasNavigationTransitionAsync(false);
                Interlocked.Exchange(ref _isAnimating, 0);
                onCompleted?.Invoke();
            }
        }

        private async Task SlideHdrIntoViewAsync(Frame frame, int duration, Action onEntranceStarted, CancellationToken cancellationToken)
        {
            double height = SafeHeight(frame);
            var translate = new TranslateTransform { Y = height };
            var slide = new DoubleAnimation
            {
                From = height,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = _easingOutFunction
            };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));
            var storyboard = new Storyboard();
            storyboard.Children.Add(slide);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storyboard.Completed += OnCompleted;
            try
            {
                frame.RenderTransform = translate;
                storyboard.Begin();
                // Opacity must stay at 1 as HDR content becomes visible.
                frame.Opacity = 1;
                onEntranceStarted();
                await done.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                storyboard.Completed -= OnCompleted;
                if (ReferenceEquals(frame.RenderTransform, translate))
                    frame.ClearValue(UIElement.RenderTransformProperty);
                storyboard.Stop();
            }

            void OnCompleted(object? sender, object args) => done.TrySetResult();
        }

        // ────────────────────────────────────────────────────────────
        //  Dismiss（overlay 滑出）
        // ────────────────────────────────────────────────────────────

        public async void Dismiss(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is not { } frame || frame.Visibility == Visibility.Collapsed) return;

            if (_cancelPendingHdrEntrance is { } cancel)
            {
                cancel();
                NotifyCompleted(onCompleted, nameof(Dismiss));
                return;
            }

            if (Interlocked.CompareExchange(ref _isAnimating, 1, 0) != 0) return;

            var playingPage = frame.Content as PlayingDetailPage;
            Storyboard? storyboard = null;
            try
            {
                if (playingPage is not null && !playingPage.TryBeginHdrDismissMotion())
                    await playingPage.SetCanvasNavigationTransitionAsync(true);

                var translate = new TranslateTransform();
                frame.RenderTransform = translate;
                var slide = new DoubleAnimation
                {
                    From = 0,
                    To = SafeHeight(frame),
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                    EasingFunction = _easingOutFunction
                };
                var opacity = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(animeTime) };
                Storyboard.SetTarget(slide, translate);
                Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));
                Storyboard.SetTarget(opacity, frame);
                Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
                storyboard = new Storyboard();
                storyboard.Children.Add(slide);
                storyboard.Children.Add(opacity);
                await RunDismissAnimationAsync(frame, storyboard);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dismiss 滑出错误");
            }

            try
            {
                frame.Visibility = Visibility.Collapsed;
                frame.Opacity = 1;
                frame.ClearValue(UIElement.RenderTransformProperty);
                storyboard?.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dismiss 归位失败");
            }

            try
            {
                if (playingPage is not null)
                {
                    playingPage.PauseCanvasRendering();
                    await playingPage.SetCanvasNavigationTransitionAsync(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dismiss 播放页清理失败");
            }
            finally
            {
                Interlocked.Exchange(ref _isAnimating, 0);
            }

            NotifyCompleted(onCompleted, nameof(Dismiss));
        }

        private static async Task RunDismissAnimationAsync(Frame frame, Storyboard storyboard)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storyboard.Completed += OnCompleted;
            frame.Unloaded += OnUnloaded;
            try
            {
                storyboard.Begin();
                await done.Task;
            }
            finally
            {
                storyboard.Completed -= OnCompleted;
                frame.Unloaded -= OnUnloaded;
            }

            void OnCompleted(object? sender, object args) => done.TrySetResult();
            void OnUnloaded(object sender, RoutedEventArgs args) => done.TrySetResult();
        }

        private void NotifyCompleted(Action? callback, string operation)
        {
            try { callback?.Invoke(); }
            catch (Exception ex) { _logger.LogError(ex, "{Operation} 完成回调失败", operation); }
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
