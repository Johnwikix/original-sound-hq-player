using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUIMusicPlayer.Model
{
    public class PlayListMusicItem : ObservableObject
    {
        public Music Music { get; set => SetProperty(ref field, value); }
        public int PlayListOrder { get; set => SetProperty(ref field, value); } = 0;
    }
}
