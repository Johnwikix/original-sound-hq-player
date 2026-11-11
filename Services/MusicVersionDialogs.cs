using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>集中持有版本管理对话框的视图边界。</summary>
public sealed class MusicVersionDialogs(System.Func<XamlRoot?>? root = null)
{
    private ContentDialog? _active;
    /// <summary>关闭本会话的在途对话框，避免退出屏障等待用户输入。</summary>
    public void Cancel() => _active?.Hide();
    /// <summary>选择已经入库的重复文件候选。</summary>
    public async Task<Music?> ChooseAsync(IReadOnlyList<Music> candidates)
    {
        var model = new MusicVersionDialogViewModel(candidates, false, ToolUtils.GetString("SongVersionsLinkDescription"));
        return await ShowAsync(model, "SongVersionsLink") ? model.Selected?.Music : null;
    }

    /// <summary>展示所有归组影响及默认歌词选择，不产生写入。</summary>
    public async Task<MusicVersionMergeDecision?> ConfirmAsync(SongMergePreview preview, IReadOnlyList<Music> versions)
    {
        int favoriteGroups = 0;
        foreach (var song in preview.State.Songs) if (song.IsFavorite) favoriteGroups++;
        HashSet<int> playlists = [];
        int duplicateEntries = 0;
        foreach (var entry in preview.State.Playlists) if (!playlists.Add(entry.PlayListId)) duplicateEntries++;
        var model = new MusicVersionDialogViewModel(versions, true, ToolUtils.GetString("SongVersionsMergeDescription"))
        {
            TargetLyrics = LyricsText(preview, preview.TargetSongId),
            OtherLyrics = LyricsText(preview, preview.OtherSongId),
            Impact = string.Format(CultureInfo.CurrentCulture, ToolUtils.GetString("SongVersionsMergeStats"),
                versions.Count, favoriteGroups, preview.State.Playlists.Count, duplicateEntries)
        };
        if (!await ShowAsync(model, "SongVersionsMergeTitle")) return null;
        return new(model.LyricsChoiceIndex == 1 ? preview.OtherSongId : preview.TargetSongId, model.CollapseDuplicates == true);
    }

    private static string LyricsText(SongMergePreview preview, int songId)
    {
        foreach (var lyrics in preview.State.Lyrics)
            if (lyrics.SongId == songId)
                return lyrics.LyricsFormat + Environment.NewLine + lyrics.Lyrics + Environment.NewLine + lyrics.TranslatedLyrics + Environment.NewLine + lyrics.PronunciationLyrics;
        return "";
    }

    private async Task<bool> ShowAsync(MusicVersionDialogViewModel model, string titleKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root?.Invoke() ?? ((FrameworkElement)App.MainWindow.Content).XamlRoot,
            Title = ToolUtils.GetString(titleKey), Content = new MusicVersionDialogControl(model),
            PrimaryButtonText = ToolUtils.GetString("SongVersionsConfirm"), CloseButtonText = ToolUtils.GetString("SongVersionsCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        _active = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { if (ReferenceEquals(_active, dialog)) _active = null; }
    }
}

/// <summary>保存用户确认的歌词来源及出现项政策。</summary>
public sealed record MusicVersionMergeDecision(int LyricsSongId, bool CollapseDuplicates);
