using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
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
        private readonly INavigationService _navigationService;
        private readonly INavigationService _playingNavigation;
        private bool _isPageTransitioning = false;
        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            // 导航服务
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            _playingNavigation = navigationServiceFactory.CreateNavigationService(PlayingFrame);
            _playingNavigation.RegisterPage<PlayingDetailPage>();
            InitiaizeEqualizerDialog();
            NavigateToDefaultPage();
            NavigationViewControl.Visibility = Visibility.Visible;
        }

        private void InitiaizeEqualizerDialog()
        {
            if (EqualizerDialog is null)
            {
                EqualizerDialog = new EqualizerDialog();
                EqualizerDialog.EqualizerGainChanged += (s, frequency) =>
                {
                    int feq = ToolUtils.FrequencyIndexMap[frequency];
                    ViewModel.PlayerCommandService.SetEqualizerGain(feq, (float)AppSettings.equalizer[frequency]);
                };
                EqualizerDialog.clearEqualizer += (s, e) =>
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
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, ViewModel.AppViewModel.EntranceAnimationTime);
                    break;
                case "MusicBrowse":
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, ViewModel.AppViewModel.EntranceAnimationTime);
                    break;
                default:
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, ViewModel.AppViewModel.EntranceAnimationTime);
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                _navigationService.FadeShow(ViewModel.AppViewModel.EntranceAnimationTime);
                _playingNavigation.Dismiss(ViewModel.AppViewModel.EntranceAnimationTime);
            }
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            _navigationService.Navigate(typeof(SettingsPage), this, null, 100);
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                _navigationService.Navigate(typeof(SettingsPage), this, null, ViewModel.AppViewModel.EntranceAnimationTime);
                _playingNavigation.Dismiss(ViewModel.AppViewModel.EntranceAnimationTime);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        _navigationService.Navigate(typeof(AddFolderPage), null, null, ViewModel.AppViewModel.EntranceAnimationTime);
                        _playingNavigation.Dismiss(ViewModel.AppViewModel.EntranceAnimationTime);
                        break;
                    case "MusicBrowse":
                        _navigationService.Navigate(typeof(MusicBrowsePage), null, null, ViewModel.AppViewModel.EntranceAnimationTime);
                        if (AppData.IsPlayingDetail)
                        {
                            NavigateToPlayingDetailPage();
                        }
                        break;
                }
            }
        }

        public void NavigateToMusicBrowsePage()
        {
            if (ContentFrame.Content is not MusicBrowsePage)
            {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                _navigationService.Navigate(typeof(MusicBrowsePage), null, null, 0);
            }
        }

        public void NavigateToPlayingDetailPage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            AppData.IsPlayingDetail = true;
            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                var pendingCount = 2;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }

                _navigationService.FadeDismiss(ViewModel.AppViewModel.EntranceAnimationTime, onCompleted: OnOneCompleted);
                _playingNavigation.Show(typeof(PlayingDetailPage), ViewModel.AppViewModel.EntranceAnimationTime, onCompleted: OnOneCompleted);
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
                var pendingCount = 2;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }
                _navigationService.FadeShow(ViewModel.AppViewModel.EntranceAnimationTime, onCompleted: OnOneCompleted);
                _playingNavigation.Dismiss(ViewModel.AppViewModel.EntranceAnimationTime, onCompleted: OnOneCompleted);
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        private void NavigationViewControl_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            App.Services.GetRequiredService<MusicBrowsePage>().BackButton();
        }

        private void KeyboardAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            App.Services.GetRequiredService<MusicBrowsePage>().BackButton();
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
