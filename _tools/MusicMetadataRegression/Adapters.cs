using System.Windows.Input;
namespace AnimatedWin2dControls.Messages { public sealed class Unused { } }
namespace WinUIMusicPlayer.Services
{
    // No real playback/UI is requested by these metadata model tests.
    public static class MusicCommands
    {
        public static ICommand PlayCommand => null!;
        public static ICommand UpdateFavouriteCommand => null!;
        public static ICommand AddToPlayListCommand => null!;
        public static void OnLyricsOffsetChanged(Model.Music music, int value) { }
    }
}
