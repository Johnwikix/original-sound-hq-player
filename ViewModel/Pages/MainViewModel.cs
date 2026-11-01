using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class MainViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public WinUIMusicPlayer.State.AppState State => AppViewModel.State;
        public BassPlayerCommandService PlayerCommandService { get; }
        public MusicBrowseViewModel MusicBrowseVM { get; }
        public DesktopLyricsViewModel DesktopLyrics { get; }
        // queueVersion 是绑定依赖：集合通知之后，队列提交完整条目顺序时再次更新可用态。
        public bool CanLocateCurrentTrack(Music? current, int itemCount, long queueVersion)
        {
            int index = AppViewModel.GetSelectedPlaybackIndex();
            return current is not null && index >= 0 && index < itemCount;
        }

        // 队列中的同一首歌可以出现多次，必须沿用条目游标，而不是按歌曲 Id 找第一项。
        public int LocateCurrentTrack() => AppViewModel.GetSelectedPlaybackIndex();

        public MainViewModel(AppViewModel appViewModel, BassPlayerCommandService playerCommandService, MusicBrowseViewModel musicBrowseVM, DesktopLyricsViewModel desktopLyrics)
        {
            AppViewModel = appViewModel;
            PlayerCommandService = playerCommandService;
            MusicBrowseVM = musicBrowseVM;
            DesktopLyrics = desktopLyrics;
        }
    }
}
