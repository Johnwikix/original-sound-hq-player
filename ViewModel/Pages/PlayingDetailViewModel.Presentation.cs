using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Pages;

public partial class PlayingDetailViewModel
{
    public bool IsBackgroundActive => BindUtils.IsNowPlayingCanvasEnabled(
        AppViewModel.IsFluidBackgroundEnabled, AppViewModel.EnableAdvancedLyricsEffect,
        AppViewModel.WindowBackgroundImagePath);

    public bool IsLyricsActive => BindUtils.IsLyricsControlEnabled(
        AppViewModel.IsFluidBackgroundEnabled, AppViewModel.EnableAdvancedLyricsEffect,
        AppViewModel.WindowBackgroundImagePath);

    public bool IsAnimatedTextActive => AppViewModel.IsWin2dAnimatedText;

    // Creation is monotonic for this page's lifetime. Turning a feature off only
    // deactivates its control; x:Load must never tear down a reusable Win2D host.
    public bool IsBackgroundCreated { get; private set => SetProperty(ref field, value); }
    public bool IsLyricsCreated { get; private set => SetProperty(ref field, value); }
    public bool IsAnimatedTextCreated { get; private set => SetProperty(ref field, value); }

    public void RefreshPresentation()
    {
        IsBackgroundCreated |= IsBackgroundActive;
        IsLyricsCreated |= IsLyricsActive;
        IsAnimatedTextCreated |= IsAnimatedTextActive;
        OnPropertyChanged(nameof(IsBackgroundActive));
        OnPropertyChanged(nameof(IsLyricsActive));
        OnPropertyChanged(nameof(IsAnimatedTextActive));
    }
}
