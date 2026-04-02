using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Foundation;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public class NavigationService : INavigationService
    {
        private readonly ConcurrentDictionary<Type, Type> _registeredPages = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly EasingFunctionBase _easingOutFunction;
        private bool _isAnimating = false;
        public Frame ContentFrame { get; set; }

        public bool CanGoBack => ContentFrame?.CanGoBack ?? false;

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _easingOutFunction = new ExponentialEase()
            {
                EasingMode = EasingMode.EaseOut,
                Exponent = 8
            };
        }

        public void Initialize(Frame frame)
        {
            ContentFrame = frame;
        }

        public void RegisterPage<T>() where T : Page
        {
            _registeredPages[typeof(T)] = typeof(T);
        }

        public void Navigate(Type pageType, object parameter = null, NavigationTransitionInfo transitionInfo = null, int animeTime = 300, bool isPlayAnime = false)
        {
            if (ContentFrame?.Content?.GetType() == pageType)
            {
                if (ContentFrame.Content is INavigatable navigatablePage && parameter is not null)
                {
                    navigatablePage.ReceiveNavigationParameter(parameter);
                }
                if (!isPlayAnime)
                {
                    return;
                }
            }

            if (_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;

                transitionInfo ??= new EntranceNavigationTransitionInfo();

                AnimatePageTransition(pageInstance, transitionInfo, animeTime);

                if (pageInstance is INavigatable navigatablePage)
                {
                    navigatablePage.ReceiveNavigationParameter(parameter);
                }
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
                // 清理旧页面残留的变换，避免旧页面 Transform 对象被持续引用
                if (ContentFrame.Content is FrameworkElement currentContent)
                {
                    currentContent.RenderTransform = null;
                    currentContent.ClearValue(UIElement.RenderTransformProperty);
                    currentContent.Opacity = 1;
                }

                ContentFrame.Content = newPage;
                ContentFrame.Visibility = Visibility.Visible;
                ContentFrame.Opacity = 1;

                if (transitionInfo is SlideNavigationTransitionInfo slideInfo)
                {
                    ExecuteSlideAnimation(newPage, slideInfo.Effect, animeTime);
                }
                else if (transitionInfo is DrillInNavigationTransitionInfo)
                {
                    ExecuteDrillInAnimation(newPage, animeTime);
                }
                else if (transitionInfo is EntranceNavigationTransitionInfo)
                {
                    ExecuteEntranceAnimation(newPage, animeTime);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NavigationService] AnimatePageTransition error: {ex.Message}");
            }
        }

        private void ExecuteSlideAnimation(Page page, SlideNavigationTransitionEffect effect, int animeTime)
        {
            var translateTransform = new TranslateTransform();
            page.RenderTransform = translateTransform;

            var animation = new DoubleAnimation()
            {
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingOutFunction
            };

            switch (effect)
            {
                case SlideNavigationTransitionEffect.FromRight:
                    translateTransform.X = ContentFrame.ActualWidth;
                    animation.From = ContentFrame.ActualWidth;
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "X");
                    break;
                case SlideNavigationTransitionEffect.FromLeft:
                    translateTransform.X = -ContentFrame.ActualWidth;
                    animation.From = -ContentFrame.ActualWidth;
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "X");
                    break;
                case SlideNavigationTransitionEffect.FromBottom:
                    translateTransform.Y = ContentFrame.ActualHeight;
                    animation.From = ContentFrame.ActualHeight;
                    animation.To = 0;
                    Storyboard.SetTarget(animation, translateTransform);
                    Storyboard.SetTargetProperty(animation, "Y");
                    break;
                default:
                    // 未知效果时直接清理，不执行动画
                    page.RenderTransform = null;
                    page.ClearValue(UIElement.RenderTransformProperty);
                    return;
            }

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            // 用具名 handler 确保能解绑，防止 storyboard 通过委托持有 page 引用
            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                storyboard.Completed -= onCompleted;
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
            };
            storyboard.Completed += onCompleted;
            storyboard.Begin();
        }

        private void ExecuteDrillInAnimation(Page page, int animeTime)
        {
            var compositeTransform = new CompositeTransform()
            {
                ScaleX = 1.1,
                ScaleY = 1.1,
            };
            page.RenderTransformOrigin = new Point(0.5, 0.5);
            page.RenderTransform = compositeTransform;
            page.Opacity = 0;

            var scaleXAnimation = new DoubleAnimation()
            {
                From = 1.1,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingOutFunction
            };
            var scaleYAnimation = new DoubleAnimation()
            {
                From = 1.1,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingOutFunction
            };
            var opacityAnimation = new DoubleAnimation()
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(animeTime)
            };

            Storyboard.SetTarget(scaleXAnimation, compositeTransform);
            Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");
            Storyboard.SetTarget(scaleYAnimation, compositeTransform);
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");
            Storyboard.SetTarget(opacityAnimation, page);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(scaleXAnimation);
            storyboard.Children.Add(scaleYAnimation);
            storyboard.Children.Add(opacityAnimation);

            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                storyboard.Completed -= onCompleted;
                // 还原 page 状态，释放 CompositeTransform 引用
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
                page.RenderTransformOrigin = new Point(0, 0);
                page.Opacity = 1;
            };
            storyboard.Completed += onCompleted;
            storyboard.Begin();
        }

        private void ExecuteEntranceAnimation(Page page, int animeTime)
        {
            var translateTransform = new TranslateTransform();
            page.RenderTransform = translateTransform;
            page.Opacity = 0;

            var translateAnimation = new DoubleAnimation()
            {
                From = 500,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingOutFunction
            };
            var opacityAnimation = new DoubleAnimation()
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(animeTime)
            };

            Storyboard.SetTarget(translateAnimation, translateTransform);
            Storyboard.SetTargetProperty(translateAnimation, "Y");
            Storyboard.SetTarget(opacityAnimation, page);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(translateAnimation);
            storyboard.Children.Add(opacityAnimation);

            EventHandler<object> onCompleted = null;
            onCompleted = (s, e) =>
            {
                storyboard.Completed -= onCompleted;
                // 修正原来错误清理 ContentFrame 的问题，改为清理 page
                page.RenderTransform = null;
                page.ClearValue(UIElement.RenderTransformProperty);
                page.Opacity = 1;
            };
            storyboard.Completed += onCompleted;
            storyboard.Begin();
        }

        public void FadeShow(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null) return;
            ContentFrame.Visibility = Visibility.Visible;

            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation()
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(animeTime),
            };
            Storyboard.SetTarget(opacityAnimation, ContentFrame);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            storyboard.Children.Add(opacityAnimation);

            EventHandler<object> onAnimCompleted = null;
            onAnimCompleted = (s, e) =>
            {
                storyboard.Completed -= onAnimCompleted;
                _isAnimating = false;
                onCompleted?.Invoke();
            };
            storyboard.Completed += onAnimCompleted;
            storyboard.Begin();
        }

        public void FadeDismiss(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null || ContentFrame.Visibility == Visibility.Collapsed) return;

            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation()
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
            };
            Storyboard.SetTarget(opacityAnimation, ContentFrame);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            storyboard.Children.Add(opacityAnimation);

            EventHandler<object> onAnimCompleted = null;
            onAnimCompleted = (s, e) =>
            {
                storyboard.Completed -= onAnimCompleted;
                ContentFrame.Visibility = Visibility.Collapsed;
                ContentFrame.Opacity = 1;
                _isAnimating = false;
                onCompleted?.Invoke();
            };
            storyboard.Completed += onAnimCompleted;
            storyboard.Begin();
        }

        public void Show(Type pageType, int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null || _isAnimating) return;
            _isAnimating = true;

            if (!_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                _isAnimating = false;
                return;
            }

            try
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;
                ContentFrame.Content = pageInstance;
                ContentFrame.Visibility = Visibility.Visible;

                var translateTransform = new TranslateTransform();
                ContentFrame.RenderTransform = translateTransform;

                var slideAnimation = new DoubleAnimation()
                {
                    From = ContentFrame.ActualHeight,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                    EasingFunction = _easingOutFunction
                };
                var opacityAnimation = new DoubleAnimation()
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                };

                Storyboard.SetTarget(slideAnimation, translateTransform);
                Storyboard.SetTargetProperty(slideAnimation, "Y");
                Storyboard.SetTarget(opacityAnimation, ContentFrame);
                Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

                var storyboard = new Storyboard();
                storyboard.Children.Add(slideAnimation);
                storyboard.Children.Add(opacityAnimation);

                EventHandler<object> onAnimCompleted = null;
                onAnimCompleted = (s, e) =>
                {
                    storyboard.Completed -= onAnimCompleted;
                    ContentFrame.RenderTransform = null;
                    ContentFrame.ClearValue(UIElement.RenderTransformProperty);
                    _isAnimating = false;
                    onCompleted?.Invoke();
                };
                storyboard.Completed += onAnimCompleted;
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NavigationService] Show error: {ex.Message}");
                _isAnimating = false;
            }
        }

        public void Dismiss(int animeTime = 300, Action? onCompleted = null)
        {
            if (ContentFrame is null || ContentFrame.Visibility == Visibility.Collapsed || _isAnimating) return;
            _isAnimating = true;

            try
            {
                var translateTransform = new TranslateTransform();
                ContentFrame.RenderTransform = translateTransform;

                var slideAnimation = new DoubleAnimation()
                {
                    From = 0,
                    To = ContentFrame.ActualHeight,
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                    EasingFunction = _easingOutFunction
                };
                var opacityAnimation = new DoubleAnimation()
                {
                    From = 1,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(animeTime),
                };

                Storyboard.SetTarget(slideAnimation, translateTransform);
                Storyboard.SetTargetProperty(slideAnimation, "Y");
                Storyboard.SetTarget(opacityAnimation, ContentFrame);
                Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

                var storyboard = new Storyboard();
                storyboard.Children.Add(slideAnimation);
                storyboard.Children.Add(opacityAnimation);

                EventHandler<object> onAnimCompleted = null;
                onAnimCompleted = (s, e) =>
                {
                    storyboard.Completed -= onAnimCompleted;
                    ContentFrame.Visibility = Visibility.Collapsed;
                    ContentFrame.Opacity = 1;
                    ContentFrame.RenderTransform = null;
                    ContentFrame.ClearValue(UIElement.RenderTransformProperty);
                    _isAnimating = false;
                    onCompleted?.Invoke();
                };
                storyboard.Completed += onAnimCompleted;
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NavigationService] Dismiss error: {ex.Message}");
                _isAnimating = false;
            }
        }

        public void GoBack()
        {
            ContentFrame?.GoBack();
        }
    }
}