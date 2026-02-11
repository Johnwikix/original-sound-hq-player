using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public class NavigationService : INavigationService
    {
        private readonly ConcurrentDictionary<Type, Type> _registeredPages = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly EasingFunctionBase _easingFunction;

        public Frame ContentFrame { get; set; }

        public bool CanGoBack => ContentFrame?.CanGoBack ?? false;

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _easingFunction = new ExponentialEase()
            {
                EasingMode = EasingMode.EaseOut,
                Exponent = 10
            };
        }

        // 添加初始化方法
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
            // 检查是否为相同页面类型，避免重复导航动画
            if (ContentFrame?.Content?.GetType() == pageType)
            {
                // 如果是相同页面但有导航参数，仍然需要传递参数
                if (ContentFrame.Content is INavigatable navigatablePage && parameter is not null)
                {
                    navigatablePage.ReceiveNavigationParameter(parameter);
                }
                if (!isPlayAnime)
                {
                    return; // 直接返回，不执行动画
                }

            }
            if (_registeredPages.TryGetValue(pageType, out var resolvedType))
            {
                var pageInstance = _serviceProvider.GetRequiredService(resolvedType) as Page;

                // 使用默认的滑动动画，如果没有指定
                transitionInfo ??= new EntranceNavigationTransitionInfo();

                // 创建动画故事板
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
                var currentContent = ContentFrame.Content as FrameworkElement;
                // 清理当前页面的变换
                if (currentContent is not null)
                {
                    currentContent.RenderTransform = null;
                    currentContent.ClearValue(UIElement.RenderTransformProperty);
                }

                // 设置新页面
                ContentFrame.Content = newPage;

                // 根据过渡信息类型执行不同动画

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
                Debug.WriteLine(ex.Message);
            }
        }

        private void ExecuteSlideAnimation(Page page, SlideNavigationTransitionEffect effect, int animeTime)
        {
            var storyboard = new Storyboard();
            var translateTransform = new TranslateTransform();
            page.RenderTransform = translateTransform;

            var animation = new DoubleAnimation()
            {
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingFunction
            };

            // 根据效果设置起始位置
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
            }

            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void ExecuteDrillInAnimation(Page page, int animeTime)
        {
            var storyboard = new Storyboard();
            var compositeTransform = new CompositeTransform()
            {
                ScaleX = 1.1,
                ScaleY = 1.1,
            };
            page.RenderTransformOrigin = new Point(0.5, 0.5);
            page.RenderTransform = compositeTransform;
            page.Opacity = 0;

            // X轴缩放动画
            var scaleXAnimation = new DoubleAnimation()
            {
                From = 1.1,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingFunction
            };

            // Y轴缩放动画（手动创建新实例）
            var scaleYAnimation = new DoubleAnimation()
            {
                From = 1.1,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingFunction
            };

            // 透明度动画
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

            storyboard.Children.Add(scaleXAnimation);
            storyboard.Children.Add(scaleYAnimation);
            storyboard.Children.Add(opacityAnimation);
            storyboard.Begin();
        }

        private void ExecuteEntranceAnimation(Page page, int animeTime)
        {
            var storyboard = new Storyboard();
            var translateTransform = new TranslateTransform() { Y = 50 };
            page.RenderTransform = translateTransform;
            page.Opacity = 0;

            var translateAnimation = new DoubleAnimation()
            {
                From = 500,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(animeTime),
                EasingFunction = _easingFunction
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

            storyboard.Children.Add(translateAnimation);
            storyboard.Children.Add(opacityAnimation);
            storyboard.Begin();
        }

        public void GoBack()
        {
            ContentFrame?.GoBack();
        }
    }
}
