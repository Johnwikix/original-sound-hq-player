using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    public class UsbWriterHelper
    {
        public EventHandler hideTransmission;
        public UsbWriterHelper()
        {
        }
        public async Task WriteToUsb(List<Music> musicList, UsbStorageDevice device)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (var music in musicList)
            {
                try
                {
                    // 替换不允许的字符为下划线
                    string sanitizedAuthor = SanitizeFileName(music.Author, invalidChars);
                    string sanitizedAlbum = SanitizeFileName(music.Album, invalidChars);

                    string targetBasePath = Path.Combine(device.Path, "MUSIC", sanitizedAuthor, sanitizedAlbum);
                    if (!Directory.Exists(targetBasePath))
                    {
                        Directory.CreateDirectory(targetBasePath);
                    }

                    string sourceFilePath = music.Path;
                    string sanitizedFileName = SanitizeFileName(Path.GetFileName(sourceFilePath), invalidChars);
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
                                File.WriteAllText(lrcFilePath, music.Lyrics);
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

        private string SanitizeFileName(string name, char[] invalidChars)
        {
            foreach (char c in invalidChars)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
