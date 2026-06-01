using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class MainViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public BassPlayerCommandService PlayerCommandService { get; }
        public MusicBrowseViewModel MusicBrowseVM { get; }
        public MainViewModel(AppViewModel appViewModel, BassPlayerCommandService playerCommandService, MusicBrowseViewModel musicBrowseVM)
        {
            AppViewModel = appViewModel;
            PlayerCommandService = playerCommandService;
            MusicBrowseVM = musicBrowseVM;
        }
    }
}
