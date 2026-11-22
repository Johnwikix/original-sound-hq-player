namespace WinUIMusicPlayer.ViewModel.Pages;

public partial class PlayingDetailViewModel
{
    public bool IsBackgroundActive => AppViewModel.IsFluidBackgroundEnabled || IsImageBackgroundActive;
    public bool IsImageBackgroundActive => !AppViewModel.IsFluidBackgroundEnabled
        && !string.IsNullOrWhiteSpace(AppViewModel.WindowBackgroundImagePath);
    public bool IsLyricsActive => !IsBackgroundActive;
    public bool UseHdrPresentation => IsBackgroundActive && AppViewModel.PlaybackHdrEnabled && AppViewModel.PlaybackHdrDisplayAvailable;
    public bool HasTransformedLyrics => AppViewModel.IsFanLyricsEnabled || AppViewModel.Is3DLyricsEnabled;

    public bool UseAcrylicLyricsPresentation { get; private set; }


    // Lazily create each host once. Only the active host subscribes to lyric updates.
    public bool IsBackgroundCreated { get; private set => SetProperty(ref field, value); }
    public bool IsLyricsCreated { get; private set => SetProperty(ref field, value); }

    public void RefreshPresentation()
    {
        IsBackgroundCreated |= IsBackgroundActive;
        IsLyricsCreated |= IsLyricsActive;
        // Keep the image surface for the lifetime of this page. Resuming an existing swap-chain
        // canvas after an acrylic surface was attached can fail during WinUI composition.
        UseAcrylicLyricsPresentation |= IsLyricsActive
            && (AppViewModel.PlaybackHdrEnabled || AppViewModel.IsPortraitLayout || HasTransformedLyrics);
        OnPropertyChanged(nameof(IsBackgroundActive));
        OnPropertyChanged(nameof(IsImageBackgroundActive));
        OnPropertyChanged(nameof(UseHdrPresentation));
        OnPropertyChanged(nameof(UseAcrylicLyricsPresentation));
        OnPropertyChanged(nameof(IsLyricsActive));
    }
}
