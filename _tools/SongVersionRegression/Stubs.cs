using System.Windows.Input;
namespace AnimatedWin2dControls.Messages { }
namespace WinUIMusicPlayer.Services
{
    public static class MusicCommands
    {
        public static ICommand PlayCommand => null!;
        public static ICommand UpdateFavouriteCommand => null!;
        public static ICommand AddToPlayListCommand => null!;
        public static ICommand OpenSourceCommand => null!;
        public static void OnLyricsOffsetChanged(WinUIMusicPlayer.Model.Music music, int value) { }
    }
}
