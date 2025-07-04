using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class SystemMediaControlsService
    {
        public SystemMediaTransportControls SystemMediaControls { get; private set; }
        private MediaPlayer mediaPlayer;
        public event EventHandler PlayRequested;
        public event EventHandler PauseRequested;
        public event EventHandler NextTrackRequested;
        public event EventHandler PreviousTrackRequested;
        public SystemMediaControlsService()
        {
            InitializeSystemMediaTransportControls();
        }

        private void InitializeSystemMediaTransportControls()
        {
            try
            {
                mediaPlayer = new MediaPlayer();
                SystemMediaControls = mediaPlayer.SystemMediaTransportControls;
                //mediaPlayer.CommandManager.IsEnabled = false;
                if (SystemMediaControls != null)
                {
                    SystemMediaControls.IsPlayEnabled = true;
                    SystemMediaControls.IsPauseEnabled = true;
                    SystemMediaControls.IsNextEnabled = true;
                    SystemMediaControls.IsPreviousEnabled = true;
                    SystemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
                    UpdateSystemMediaControlsState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化 SMTC 失败: {ex.Message}");
            }
        }

        private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    PlayRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextTrackRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PreviousTrackRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        public void UpdateSystemMediaControlsState()
        {
            SystemMediaControls.PlaybackStatus = AppSettings.isPlaying ?
                MediaPlaybackStatus.Playing :
                MediaPlaybackStatus.Paused;
        }

        //public async Task UpdateMediaInfo(string title, string artist, string album, Image cover = null)
        //{
        //    var updater = SystemMediaControls.DisplayUpdater;
        //    updater.Type = MediaPlaybackType.Music;
        //    var musicProperties = updater.MusicProperties;
        //    updater.MusicProperties.Title = title;
        //    updater.MusicProperties.Artist = artist;
        //    updater.MusicProperties.AlbumTitle = album;
        //    if (cover != null)
        //    {
        //        try
        //        {
        //            updater.Thumbnail = await ImageToRandomAccessStreamReferenceAsync(cover);
        //        }
        //        catch (Exception ex)
        //        {
        //            System.Diagnostics.Debug.WriteLine($"设置专辑封面失败: {ex.Message}");
        //        }
        //    }
        //    else
        //    {
        //        updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri("ms-appx:///Assets/Music.png"));
        //    }
        //    updater.Update();
        //}

        public async Task UpdateMediaInfo(string title, string artist, string album, byte[] cover = null)
        {
            var updater = SystemMediaControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            var musicProperties = updater.MusicProperties;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            if (cover != null && cover.Length > 0)
            {
                try
                {
                    updater.Thumbnail = await ByteArrayToRandomAccessStreamReferenceAsync(cover);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"设置专辑封面失败: {ex.Message}");
                }
            }
            else
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri("ms-appx:///Assets/Album.png"));
            }
            updater.Update();
        }

        public static async Task<RandomAccessStreamReference> ByteArrayToRandomAccessStreamReferenceAsync(byte[] imageBytes)
        {
            try
            {
                // 空值检查
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    Console.WriteLine("输入的字节数组为空或null");
                    return null;
                }
                // 创建内存流
                InMemoryRandomAccessStream memoryStream = new InMemoryRandomAccessStream();

                // 将字节数组写入内存流
                using (DataWriter writer = new DataWriter(memoryStream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(imageBytes);
                    await writer.StoreAsync();
                }

                // 重置流位置到开始
                memoryStream.Seek(0);

                // 创建 RandomAccessStreamReference
                RandomAccessStreamReference streamReference = RandomAccessStreamReference.CreateFromStream(memoryStream);
                return streamReference;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"转换过程中出现错误: {ex.Message}");
                return null;
            }
        }

        //public static async Task<RandomAccessStreamReference> ImageToRandomAccessStreamReferenceAsync(Image image)
        //{
        //    try
        //    {
        //        // 创建 RenderTargetBitmap 来渲染 Image 控件
        //        RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap();
        //        await renderTargetBitmap.RenderAsync(image);
        //        // 获取像素数据
        //        IBuffer pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
        //        byte[] pixels = new byte[pixelBuffer.Length];
        //        using (DataReader reader = DataReader.FromBuffer(pixelBuffer))
        //        {
        //            reader.ReadBytes(pixels);
        //        }
        //        // 创建内存流
        //        InMemoryRandomAccessStream memoryStream = new InMemoryRandomAccessStream();

        //        // 创建编码器
        //        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);

        //        // 设置编码器的像素数据
        //        encoder.SetPixelData(
        //            BitmapPixelFormat.Bgra8,
        //            BitmapAlphaMode.Premultiplied,
        //            (uint)renderTargetBitmap.PixelWidth,
        //            (uint)renderTargetBitmap.PixelHeight,
        //            96,
        //            96,
        //            pixels);
        //        // 保存编码后的数据到内存流
        //        await encoder.FlushAsync();
        //        // 创建 RandomAccessStreamReference
        //        RandomAccessStreamReference streamReference = RandomAccessStreamReference.CreateFromStream(memoryStream);
        //        return streamReference;
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"转换过程中出现错误: {ex.Message}");
        //        return null;
        //    }
        //}
    }
}
