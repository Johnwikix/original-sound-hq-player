using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper
{
    public class UsbWriterHelper : IDisposable
    {
        public EventHandler hideTransmission;
        private bool _disposed = false;
        public UsbWriterHelper()
        {
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 释放托管资源
                    UnsubscribeEvents();
                }

                // 释放非托管资源（如果有）
                // 例如：关闭文件句柄、网络连接等

                _disposed = true;
            }
        }

        private void UnsubscribeEvents()
        {
            // 取消所有事件订阅，防止内存泄漏
            if (hideTransmission is not null)
            {
                foreach (var handler in hideTransmission.GetInvocationList())
                {
                    hideTransmission -= (EventHandler)handler;
                }
            }
        }

        public async Task WriteToUsb(IEnumerable<Music> musicList, UsbStorageDevice device)
        {
            // 检查对象是否已被释放
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UsbWriterHelper));
            }
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (var music in musicList)
            {
                try
                {
                    // 替换不允许的字符为下划线
                    string sanitizedAuthor = ToolUtils.SanitizeFileName(music.Author, invalidChars);
                    string sanitizedAlbum = ToolUtils.SanitizeFileName(music.Album, invalidChars);

                    string targetBasePath = Path.Combine(device.Path, "MUSIC", sanitizedAuthor, sanitizedAlbum);
                    if (!Directory.Exists(targetBasePath))
                    {
                        Directory.CreateDirectory(targetBasePath);
                    }

                    string sourceFilePath = music.Path;
                    string sanitizedFileName = ToolUtils.SanitizeFileName(Path.GetFileName(sourceFilePath), invalidChars);
                    string targetFilePath = Path.Combine(targetBasePath, sanitizedFileName);

                    if (File.Exists(sourceFilePath))
                    {
                        await Task.Run(() =>
                        {
                            File.Copy(sourceFilePath, targetFilePath, true);
                        });
                        Console.WriteLine($"已将 {sourceFilePath} 复制到 {targetFilePath}");
                        if (!string.IsNullOrEmpty(music.Lyrics))
                        {
                            await Task.Run(() =>
                            {
                                string lrcFileName = Path.ChangeExtension(sanitizedFileName, ".lrc");
                                string lrcFilePath = Path.Combine(targetBasePath, lrcFileName);
                                File.WriteAllText(lrcFilePath, ToolUtils.ConvertLyrics(music.Lyrics));
                                Console.WriteLine($"已创建歌词文件: {lrcFilePath}");
                            });
                        }
                    }
                    else
                    {
                        Console.WriteLine($"源文件 {sourceFilePath} 不存在，无法复制。");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"在将 {music.Path} 复制到 {device.Name} 设备时发生错误: {ex.Message}");
                }
            }
            hideTransmission?.Invoke(this, EventArgs.Empty);
        }




    }
}
