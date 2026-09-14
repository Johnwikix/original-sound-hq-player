using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public class InitialFileScan
{
    public static async Task InitialScan()
    {
        var database = App.Services.GetRequiredService<MusicDatabaseService>();
        foreach (var folder in await database.GetFolders())
        {
            if (string.IsNullOrEmpty(folder.Path)) continue;
            try { await database.ScanChangedFolderAsync(folder.Path); }
            catch (Exception ex)
            {
                // A missing drive or inaccessible subtree is not evidence that its music was deleted.
                App.GetLogger<InitialFileScan>().LogWarning(ex, "启动扫描未完成，保留旧记录: {Path}", folder.Path);
            }
        }
        await Deduplication();
    }

    public static async Task Deduplication()
    {
        var database = App.Services.GetRequiredService<MusicDatabaseService>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<Music>();
        foreach (var music in await database.GetMusicListAsync())
            if (!seen.Add(music.Path)) duplicates.Add(music);
        await database.DeletedMusicList(duplicates);
    }
}
