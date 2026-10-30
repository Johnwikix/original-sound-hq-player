using DevWinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.Services;

/// <summary>浏览交互的视图边界；不缓存页面。延迟取得应用级外壳，避免其构造链与浏览 VM 循环依赖。</summary>
public sealed class LibraryNavigationService(Func<MainPage> getShell, ILogger<LibraryNavigationService> logger)
{
    public void ShowPlayingDetail() => getShell().NavigateToPlayingDetailPage();

    public async Task ShowEqualizerAsync()
    {
        var shell = getShell();
        await shell.EqualizerDialog.ShowThemedAsync(shell.XamlRoot);
    }

    public void NavigatePage(Type pageType, object? parameter = null, NavigationTransitionInfo? transition = null)
        => getShell().CurrentLibraryPage?.NavigatePage(pageType, parameter, transition);

    public void Back() => getShell().CurrentLibraryPage?.BackButton();

    public void SelectAlbum(string album)
    {
        var shell = getShell();
        shell.NavigateToMusicBrowsePage();
        shell.CurrentLibraryPage?.SelectBarAlbum(album);
    }

    public async Task SelectArtistAsync(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        try
        {
            var shell = getShell();
            var names = ArtistHelper.GetArtistNames(artist);
            if (names.Length > 1)
            {
                if (shell.XamlRoot is null) return;
                artist = await DialogHelper.ShowArtistPickerAsync(shell.XamlRoot, names) ?? string.Empty;
                if (artist.Length == 0) return;
            }
            shell.NavigateToMusicBrowsePage();
            shell.CurrentLibraryPage?.SelectBarArtist(artist);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开艺术家页面失败");
        }
    }

    public Task<bool> ConfirmDeleteFromDiskAsync()
        => DialogHelper.ShowConfirmAsync(getShell().XamlRoot, "AreUSureDeleteFromDisk");
}
