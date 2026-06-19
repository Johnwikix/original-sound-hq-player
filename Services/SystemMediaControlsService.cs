using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class SystemMediaControlsService
    {
        public SystemMediaTransportControls SystemMediaControls { get; private set; }
        private readonly SystemMediaTransportControlsTimelineProperties _timelineProperties = new();
        private MediaPlayer mediaPlayer;
        public event EventHandler PlayRequested;
        public event EventHandler PauseRequested;
        public event EventHandler NextTrackRequested;
        public event EventHandler PreviousTrackRequested;
        private ILogger<SystemMediaControlsService> _logger;
        public SystemMediaControlsService(ILogger<SystemMediaControlsService> logger)
        {
            _logger = logger;
            InitializeSystemMediaTransportControls();
        }

        private void InitializeSystemMediaTransportControls()
        {
            try
            {
                mediaPlayer = new MediaPlayer();
                SystemMediaControls = mediaPlayer.SystemMediaTransportControls;
                mediaPlayer.CommandManager.IsEnabled = true;
                if (SystemMediaControls is not null)
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
                    SystemMediaControls.PlaybackPositionChangeRequested += SystemMediaControls_PlaybackPositionChangeRequested;
                    UpdateSystemMediaControlsState();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"InitializeSystemMediaTransportControls 初始化 SMTC 失败: {ex.Message}");
            }
        }

        // 处理播放位置更改请求
        private void SystemMediaControls_PlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        {
            App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime((long)args.RequestedPlaybackPosition.TotalMilliseconds);
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
            SystemMediaControls.PlaybackStatus = AppData.IsPlaying ?
                MediaPlaybackStatus.Playing :
                MediaPlaybackStatus.Paused;
        }


        public async Task UpdateMediaInfo(string title, string artist, string album, byte[] cover = null)
        {
            SystemMediaControls.DisplayUpdater.Type = MediaPlaybackType.Music;
            SystemMediaControls.DisplayUpdater.MusicProperties.Title = title;
            SystemMediaControls.DisplayUpdater.MusicProperties.Artist = artist;
            SystemMediaControls.DisplayUpdater.MusicProperties.AlbumTitle = album;
            if (cover is not null && cover.Length > 0)
            {
                try
                {
                    SystemMediaControls.DisplayUpdater.Thumbnail = await ByteArrayToRandomAccessStreamReferenceAsync(cover);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"UpdateMediaInfo 设置专辑封面失败: {ex.Message}");
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
                if (SystemMediaControls is null || _timelineProperties is null) return;
                _timelineProperties.StartTime = TimeSpan.Zero;
                _timelineProperties.EndTime = totalDuration;
                _timelineProperties.Position = currentPosition;
                _timelineProperties.MinSeekTime = TimeSpan.Zero;
                _timelineProperties.MaxSeekTime = totalDuration;
                SystemMediaControls.UpdateTimelineProperties(_timelineProperties);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateTimelineProperties 更新时间轴失败: {ex.Message}");
            }
        }

        public static async Task<RandomAccessStreamReference?> ByteArrayToRandomAccessStreamReferenceAsync(byte[] imageBytes)
        {
            try
            {
                if (imageBytes is null || imageBytes.Length == 0)
                    return null;

                var memoryStream = new InMemoryRandomAccessStream();
                await memoryStream.WriteAsync(imageBytes.AsBuffer());
                memoryStream.Seek(0);
                return RandomAccessStreamReference.CreateFromStream(memoryStream);
            }
            catch (Exception ex)
            {
                App.GetLogger<SystemMediaControlsService>().LogError(ex, "ByteArrayToRandomAccessStreamReferenceAsync 转换过程中出现错误");
                return null;
            }
        }
    }
}
