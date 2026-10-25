using System;
using System.Diagnostics;
using System.IO;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

public static class OneShotLyricsCache
{
    private static readonly Lazy<LyricsCacheStore> Store = new(() => new(
        Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "OneShotLyricsCache"), new LyricsParser()));

    public static LyricsCacheEntry? Load(string path)
    {
        try { return Store.Value.Load(path); }
        catch (Exception ex) { Debug.WriteLine($"Lyrics cache read failed: {ex.Message}"); return null; }
    }

    public static void Save(string path, LyricsDocument document)
    {
        try { Store.Value.Save(path, document); }
        catch (Exception ex) { Debug.WriteLine($"Lyrics cache write failed: {ex.Message}"); }
    }
    public static bool SaveEdited(string path, LyricsDocument document, long expectedRevision) =>
        Store.Value.Save(path, document, expectedRevision, sourceKind: "User");

    public static bool TrySave(string path, LyricsDocument document, long expectedRevision)
    {
        try { return Store.Value.Save(path, document, expectedRevision); }
        catch (Exception ex) { Debug.WriteLine($"Lyrics cache write failed: {ex.Message}"); return false; }
    }

}
