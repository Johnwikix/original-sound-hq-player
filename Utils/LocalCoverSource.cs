using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils;

internal static class LocalCoverSource
{
    private static readonly string[] Names =
        ["cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png", "front.jpg", "front.jpeg", "front.png"];
    private const int ByteLimit = 16 * 1024 * 1024;
    private sealed record Snapshot(string Directory, string Metadata, string Revision, long Checked);
    private sealed class Slot
    {
        internal Snapshot? Snapshot;
        internal readonly SemaphoreSlim Reader = new(1, 1);
        internal string? AudioPath;
        internal string? Revision;
        internal DateTime AudioTime;
        internal WeakReference<byte[]>? Picture;
    }

    // Bounded independently of library size. The weak reference shares a cold read among active consumers.
    private static readonly Slot[] Slots = Array.ConvertAll(new int[32], static _ => new Slot());
    private static Slot SlotFor(string key) => Slots[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(key) % Slots.Length];

    internal static bool IsCandidate(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        foreach (string candidate in Names)
            if (fileName.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string GetRevision(string directory, bool verifyContent, CancellationToken token, IReadOnlyList<string>? candidatePaths = null)
    {
        if (string.IsNullOrEmpty(directory)) return "";
        var slot = SlotFor(directory);
        lock (slot)
        {
            var previous = slot.Snapshot;
            if (!string.Equals(previous?.Directory, directory, StringComparison.OrdinalIgnoreCase)) previous = null;
            long now = Environment.TickCount64;
            if (!verifyContent && previous is not null && now - previous.Checked < 1500) return previous.Revision;
            List<FileInfo>? candidates = null;
            foreach (string name in Names)
            {
                token.ThrowIfCancellationRequested();
                if (candidatePaths is not null && !ContainsName(candidatePaths, name)) continue;
                var info = new FileInfo(Path.Combine(directory, name));
                if (info.Exists && info.Length is > 0 and <= ByteLimit) (candidates ??= []).Add(info);
            }
            string metadata = candidates is null ? "" : string.Join("|", candidates.ConvertAll(
                static f => string.Concat(f.Name, "/", f.Length, "/", f.LastWriteTimeUtc.Ticks)));
            string revision = "";
            if (!verifyContent && previous?.Metadata == metadata) revision = previous.Revision;
            else if (candidates is not null)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Span<byte> buffer = stackalloc byte[8192];
                foreach (var info in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    using var file = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (file.Length != info.Length) throw new IOException("Cover changed while being inspected.");
                    int read;
                    while ((read = file.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        hash.AppendData(buffer[..read]);
                    }
                }
                revision = metadata + "|" + Convert.ToHexString(hash.GetHashAndReset());
            }
            slot.Snapshot = new(directory, metadata, revision, now);
            return revision;
        }
    }

    private static bool ContainsName(IReadOnlyList<string> paths, string name)
    {
        foreach (string path in paths)
            if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsCacheCurrent(Music music, string cache)
    {
        if (music.IsRemote) return true;
        if (!string.IsNullOrEmpty(music.CoverOverridePath) && File.Exists(music.CoverOverridePath))
            return music.ImageHash == Path.GetFileNameWithoutExtension(music.CoverOverridePath);
        long audioTicks = File.GetLastWriteTimeUtc(music.Path).Ticks;
        return GetRevision(Path.GetDirectoryName(music.Path) ?? "", false, default) == (music.CoverSourceStamp ?? "")
            && (music.CoverAudioWriteTicks == audioTicks || music.CoverAudioWriteTicks == 0 && File.GetLastWriteTimeUtc(cache).Ticks > audioTicks);
    }

    internal static async Task<byte[]> ReadAsync(Music music, string revision, Func<Music, byte[]> embedded, CancellationToken token)
    {
        var slot = SlotFor(music.Path);
        await slot.Reader.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var audioTime = File.GetLastWriteTimeUtc(music.Path);
            if (slot.AudioPath == music.Path && slot.AudioTime == audioTime && slot.Revision == revision
                && slot.Picture is not null && slot.Picture.TryGetTarget(out var cached)) return cached;
            byte[] bytes = embedded(music);
            if (bytes.Length == 0 && revision.Length != 0 && Path.GetDirectoryName(music.Path) is { } directory)
            {
                foreach (string name in Names)
                {
                    token.ThrowIfCancellationRequested();
                    var info = new FileInfo(Path.Combine(directory, name));
                    if (!info.Exists || info.Length is <= 0 or > ByteLimit) continue;
                    try
                    {
                        using var file = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        if (file.Length is <= 0 or > ByteLimit) continue;
                        using var stream = file.AsRandomAccessStream();
                        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
                        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 64_000_000) continue;
                        using var preview = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                            new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1 }, ExifOrientationMode.IgnoreExifOrientation,
                            ColorManagementMode.DoNotColorManage).AsTask(token).ConfigureAwait(false);
                        file.Position = 0;
                        var candidate = new byte[(int)file.Length];
                        await file.ReadExactlyAsync(candidate, token).ConfigureAwait(false);
                        bytes = candidate;
                        break;
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        System.Diagnostics.Debug.WriteLine($"Cannot decode directory cover {info.Name}: {ex.Message}");
                    }
                }
            }
            slot.AudioPath = music.Path;
            slot.AudioTime = audioTime;
            slot.Revision = revision;
            slot.Picture = new(bytes);
            return bytes;
        }
        finally { slot.Reader.Release(); }
    }
}
