using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Helper;

/// <summary>将已取得的原始封面发布到大图控件使用的缓存；目标文件始终是完整内容。</summary>
internal static class PlaybackCoverCache
{
    /// <summary>复用调用方字节，不重新取图；调用方应在后台执行并在完成后发布封面标识。</summary>
    public static async Task StoreAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsComplete(path, bytes.Length)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, true); }
            catch (IOException) when (IsComplete(path, bytes.Length))
            {
                // 相同版本的另一请求已发布，且目标可能正在被 UI 解码器读取。
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsComplete(string path, int length)
        => File.Exists(path) && new FileInfo(path).Length == length;
}
