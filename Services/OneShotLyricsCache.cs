using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinUIMusicPlayer.Services;

/// <summary>缓存条目：在线搜索到的歌词原文（KRC/QRC 与 LRC 分开存，翻译独立保存）。</summary>
public sealed class OneShotLyricsCacheEntry
{
    /// <summary>明文路径：读取时校验哈希碰撞，不匹配按未命中处理。</summary>
    public string Path { get; set; } = string.Empty;
    public string? Krc { get; set; }
    public string? TKrc { get; set; }
    public string? Lrc { get; set; }
    public string? Trans { get; set; }
    public DateTime CachedAtUtc { get; set; }
}

/// <summary>
/// 外部文件一次性播放的独立歌词缓存：按文件路径哈希存 JSON，目录与封面缓存并列
/// （LocalFolder/OneShotLyricsCache），不触碰数据库。命中即免在线搜索；损坏/碰撞条目按未命中删除；
/// 超过条目上限时按最后写入时间淘汰最旧。缓存是非关键加速路径，任何读写失败都不影响播放。
/// </summary>
public static class OneShotLyricsCache
{
    private static readonly string CacheDirectory = Path.Combine(
        Windows.Storage.ApplicationData.Current.LocalFolder.Path, "OneShotLyricsCache");
    // 每条数 KB，300 条上限约几 MB；写入时淘汰，无独立清理任务。
    private const int MaxEntries = 300;

    public static OneShotLyricsCacheEntry? Load(string musicPath)
    {
        try
        {
            string file = GetEntryPath(musicPath);
            if (!File.Exists(file)) return null;
            var entry = JsonSerializer.Deserialize(
                File.ReadAllText(file),
                Helper.AppJsonSerializerContextHelper.Default.OneShotLyricsCacheEntry);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)
                || !string.Equals(entry.Path, musicPath, StringComparison.OrdinalIgnoreCase))
            {
                // 哈希碰撞或半写损坏：当未命中并移除，下次搜索后重建。
                TryDelete(file);
                return null;
            }
            return entry;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OneShotLyricsCache.Load 失败: {ex.Message}");
            return null;
        }
    }

    public static void Save(string musicPath, string? krc, string? tKrc, string? lrc, string? trans)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var entry = new OneShotLyricsCacheEntry
            {
                Path = musicPath,
                Krc = krc,
                TKrc = tKrc,
                Lrc = lrc,
                Trans = trans,
                CachedAtUtc = DateTime.UtcNow,
            };
            string file = GetEntryPath(musicPath);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                entry, Helper.AppJsonSerializerContextHelper.Default.OneShotLyricsCacheEntry));
            File.Move(tmp, file, overwrite: true);
            TrimToLimit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OneShotLyricsCache.Save 失败: {ex.Message}");
        }
    }

    private static string GetEntryPath(string musicPath)
    {
        // Windows 路径大小写不敏感，统一大写后哈希，避免同一文件因大小写差异产生双份缓存。
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(musicPath.ToUpperInvariant())));
        return Path.Combine(CacheDirectory, hash + ".json");
    }

    private static void TrimToLimit()
    {
        List<(string Path, DateTime WriteTimeUtc)> entries = [];
        foreach (string file in Directory.EnumerateFiles(CacheDirectory, "*.json"))
        {
            DateTime writeTimeUtc;
            try { writeTimeUtc = File.GetLastWriteTimeUtc(file); }
            catch (Exception ex) { Debug.WriteLine($"OneShotLyricsCache 读取时间失败: {ex.Message}"); continue; }
            entries.Add((file, writeTimeUtc));
        }
        if (entries.Count <= MaxEntries) return;
        foreach (var oldest in entries.OrderBy(t => t.WriteTimeUtc).Take(entries.Count - MaxEntries))
            TryDelete(oldest.Path);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) { Debug.WriteLine($"OneShotLyricsCache 删除失败: {ex.Message}"); }
    }
}
