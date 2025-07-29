using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
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

                    // 启用时间轴控制
                    SystemMediaControls.IsChannelUpEnabled = false;
                    SystemMediaControls.IsChannelDownEnabled = false;
                    SystemMediaControls.IsStopEnabled = false;
                    SystemMediaControls.IsRecordEnabled = false;
                    SystemMediaControls.IsFastForwardEnabled = false;
                    SystemMediaControls.IsRewindEnabled = false;
                    SystemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
                    //SystemMediaControls.PlaybackPositionChangeRequested += SystemMediaControls_PlaybackPositionChangeRequested;
                    UpdateSystemMediaControlsState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化 SMTC 失败: {ex.Message}");
            }
        }

        // 处理播放位置更改请求
        private void SystemMediaControls_PlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        {
            App.Services.GetRequiredService<MusicPlaybackService>().ChangeWaveChannelTime(args.RequestedPlaybackPosition);
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
       

        public async Task UpdateMediaInfo(string title, string artist, string album, byte[] cover = null)
        {
            SystemMediaControls.DisplayUpdater.Type = MediaPlaybackType.Music;
            SystemMediaControls.DisplayUpdater.MusicProperties.Title = title;
            SystemMediaControls.DisplayUpdater.MusicProperties.Artist = artist;
            SystemMediaControls.DisplayUpdater.MusicProperties.AlbumTitle = album;
            if (cover != null && cover.Length > 0)
            {
                try
                {
                    SystemMediaControls.DisplayUpdater.Thumbnail = await ByteArrayToRandomAccessStreamReferenceAsync(cover);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"设置专辑封面失败: {ex.Message}");
                }
            }
            else
            {
                SystemMediaControls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri("ms-appx:///Assets/Album.png"));
            }
            SystemMediaControls.DisplayUpdater.Update();
        }

        public void UpdateTimelineProperties(TimeSpan currentPosition, TimeSpan totalDuration)
        {
            try
            {
                if (SystemMediaControls == null) return;
                var timelineProperties = new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = totalDuration,
                    Position = currentPosition,
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = totalDuration
                };
                SystemMediaControls.UpdateTimelineProperties(timelineProperties);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新时间轴失败: {ex.Message}");
            }
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
                    await writer.StoreAsync().AsTask().ConfigureAwait(false);
                    await writer.FlushAsync().AsTask().ConfigureAwait(false);
                }
                // 重置流位置到开始
                memoryStream.Seek(0);
                return RandomAccessStreamReference.CreateFromStream(memoryStream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"转换过程中出现错误: {ex.Message}");
                return null;
            }
        }        
    }
}
