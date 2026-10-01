using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

public static class LyricsExporter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>只写翻译侧车文件，避免自动翻译重新覆盖用户原始歌词文件。</summary>
    public static async Task<string> SaveTranslationFileAsync(string musicPath, string translationLrc, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) throw new ArgumentException("Music path is required.", nameof(musicPath));
        if (string.IsNullOrWhiteSpace(translationLrc)) throw new ArgumentException("Translation is empty.", nameof(translationLrc));
        string? folder = Path.GetDirectoryName(musicPath);
        if (folder is null) throw new IOException("The music file has no parent directory.");
        string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(musicPath) + "_Translated.lrc");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, translationLrc, new UTF8Encoding(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return target;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            Gate.Release();
        }
    }

    public static async Task<string> SaveFilesAsync(string musicPath, LyricsDocument document, CancellationToken token)
    {
        if (document.Original.Format == LyricsFormat.Unknown) throw new FormatException("The original lyrics format cannot be exported.");
        string original = Path.ChangeExtension(musicPath, LyricsFilePolicy.Extension(document.Original.Format));
        string translation = Path.Combine(Path.GetDirectoryName(musicPath)!, Path.GetFileNameWithoutExtension(musicPath) + "_Translated.lrc");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        var staged = new List<(string Target, string Temp, string Backup, bool Existed, bool Delete)>();
        var committed = new List<(string Target, string Backup, bool Existed)>();
        try
        {
            foreach (var item in new[] { (original, document.Original.Content), (translation, document.TranslationLrc) })
            {
                token.ThrowIfCancellationRequested();
                string temporary = item.Item1 + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool existed = File.Exists(item.Item1);
                string backup = item.Item1 + ".lyrics.bak";
                var entry = (item.Item1, temporary, backup, existed, string.IsNullOrEmpty(item.Item2));
                staged.Add(entry);
                if (!entry.Item5) await File.WriteAllTextAsync(temporary, item.Item2, new UTF8Encoding(false), token).ConfigureAwait(false);
                if (existed) File.Copy(item.Item1, backup, true);
            }
            token.ThrowIfCancellationRequested();
            // Individual files replace atomically; rollback restores the pair if the second replacement fails.
            foreach (var item in staged)
            {
                if (item.Delete) { if (item.Existed) File.Delete(item.Target); }
                else File.Move(item.Temp, item.Target, true);
                committed.Add((item.Target, item.Backup, item.Existed));
            }
            return original;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            for (int i = committed.Count - 1; i >= 0; i--)
            {
                var item = committed[i];
                try { if (item.Existed) File.Copy(item.Backup, item.Target, true); else File.Delete(item.Target); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
            }
            if (errors.Count > 1) throw new AggregateException("Lyrics export rollback failed; backups were preserved.", errors);
            throw;
        }
        finally
        {
            foreach (var item in staged)
                try { if (File.Exists(item.Temp)) File.Delete(item.Temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Gate.Release();
        }
    }
}
