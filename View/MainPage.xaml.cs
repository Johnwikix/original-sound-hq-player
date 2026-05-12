using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }
        public EqualizerDialog EqualizerDialog { get; set; }
        private readonly INavigationService _playingNavigation;
        private bool _isPageTransitioning = false;
        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            // 导航服务
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _playingNavigation = navigationServiceFactory.CreateNavigationService(PlayingFrame);
            _playingNavigation.RegisterPage<PlayingDetailPage>();
            InitiaizeEqualizerDialog();
            NavigateToDefaultPage();
            NavigationViewControl.Visibility = Visibility.Visible;
        }

        private void NavigateTo(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo =null)
        {
            MainFrame.Navigate(pageType, parameter, navigationTransitionInfo);
            MainFrame.BackStack.Clear();
        }

        private void InitiaizeEqualizerDialog()
        {
            if (EqualizerDialog is null)
            {
                EqualizerDialog = new EqualizerDialog();
                EqualizerDialog.EqualizerGainChanged += (s, frequency) =>
                {
                    int feq = ToolUtils.FrequencyIndexMap[frequency];
                    ViewModel.PlayerCommandService.SetEqualizerGain(feq, (float)AppSettings.Equalizer[frequency]);
                };
                EqualizerDialog.ClearEqualizer += (s, e) =>
                {
                    ViewModel.PlayerCommandService.UpdateSettings();
                    if (AppSettings.IsEqualizerEnabled)
                    {
                        ViewModel.PlayerCommandService.ToggleEqualizer();
                        ViewModel.PlayerCommandService.SetEqualizer();
                    }
                    else
                    {
                        ViewModel.PlayerCommandService.ClearEqualizer();
                    }
                };
            }
        }

        private void NavigateToDefaultPage()
        {

            foreach (var item in NavigationViewControl.MenuItems)
            {
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == ViewModel.AppViewModel.DefaultEntryComboBoxTag)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (ViewModel.AppViewModel.DefaultEntryComboBoxTag)
            {
                case "AddFolder":
                    NavigateTo(typeof(AddFolderPage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "MusicBrowse":
                    NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                    break;
                default:
                    NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                MainFrame.Visibility = Visibility.Visible;
                _playingNavigation.Dismiss(300);
            }
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            NavigateTo(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                NavigateTo(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
                _playingNavigation.Dismiss(300);
                MainFrame.Visibility = Visibility.Visible;
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        NavigateTo(typeof(AddFolderPage), null, new EntranceNavigationTransitionInfo());
                        _playingNavigation.Dismiss(300);
                        MainFrame.Visibility = Visibility.Visible;
                        break;
                    case "MusicBrowse":
                        NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                        if (AppData.IsPlayingDetail)
                        {                            
                            NavigateToPlayingDetailPage();
                        }
                        else {
                            MainFrame.Visibility = Visibility.Visible;
                        }
                        break;
                }
            }
        }

        public void NavigateToMusicBrowsePage()
        {
            if (MainFrame.Content is not MusicBrowsePage)
            {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
            }
        }

        public void NavigateToPlayingDetailPage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            AppData.IsPlayingDetail = true;
            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }
                MainFrame.Visibility = Visibility.Collapsed;              
                _playingNavigation.Show(typeof(PlayingDetailPage), 300, onCompleted: OnOneCompleted);
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        public void NavigatebackToMusicBrowsePage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            AppData.IsPlayingDetail = false;
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }
                MainFrame.Visibility = Visibility.Visible;
                //ContentFrame.FadeShow(ViewModel.AppViewModel.EntranceAnimationTime, onCompleted: OnOneCompleted);
                _playingNavigation.Dismiss(300, onCompleted: OnOneCompleted);
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        private void NavigationViewControl_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            App.Services.GetRequiredService<MusicBrowseViewModel>().BackButton();
        }

        private void KeyboardAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            App.Services.GetRequiredService<MusicBrowseViewModel>().BackButton();
        }

        private void NavigationViewControl_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsInNaviView = true;
        }

        private void NavigationViewControl_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsInNaviView = false;
        }
    }
}
