using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == AppSettings.DefualtEntry)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (AppSettings.DefualtEntry)
            {
                case "AddFolder":
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                    break;
                case "MusicBrowse":
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
                    break;
                default:
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                _navigationService.FadeShow(AppSettings.EntranceAnimationTime);
                _playingNavigation.Dismiss(AppSettings.EntranceAnimationTime);
            }
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            _navigationService.Navigate(typeof(SettingsPage), this, null, 100);
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                _navigationService.Navigate(typeof(SettingsPage), this, null, AppSettings.EntranceAnimationTime);
                _playingNavigation.FadeDismiss(AppSettings.EntranceAnimationTime);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                        _playingNavigation.FadeDismiss(AppSettings.EntranceAnimationTime);
                        break;
                    case "MusicBrowse":
                        _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
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
            AppData.IsPlayingDetail = true;
            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                _navigationService.FadeDismiss(AppSettings.EntranceAnimationTime);
                _playingNavigation.Show(typeof(PlayingDetailPage), AppSettings.EntranceAnimationTime);
            }
        }

        public void NavigatebackToMusicBrowsePage()
        {
            AppData.IsPlayingDetail = false;
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                _navigationService.FadeShow(AppSettings.EntranceAnimationTime);
                _playingNavigation.Dismiss(AppSettings.EntranceAnimationTime);
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
