namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    public string RemotePlaybackStatus { get; set => SetProperty(ref field, value); } = "";
}
