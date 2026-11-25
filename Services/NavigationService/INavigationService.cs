using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace WinUIMusicPlayer.Services.NavigationService
{
    public interface INavigationService
    {
        Frame ContentFrame { get; set; }
        void RegisterPage<T>() where T : Page;
        void Navigate(Type pageType, object parameter = null, NavigationTransitionInfo transitionInfo = null, int animeTime = 300, bool isPlayAnime = false);
        bool IsPreparingShow { get; }
        void Show(Type pageType, int animeTime = 300, Action? onCompleted = null, Action? onRevealing = null,
            Action? onEntranceStarted = null, Action? onPreparingHdr = null);
        void Dismiss(int animeTime = 300, Action? onCompleted = null);
        void FadeShow(int animeTime = 300, Action? onCompleted = null);
        void FadeDismiss(int animeTime = 300, Action? onCompleted = null);
        void Initialize(Frame frame);
        void GoBack();
        bool CanGoBack { get; }
    }
}
