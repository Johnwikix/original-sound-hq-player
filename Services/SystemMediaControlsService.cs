using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class SystemMediaControlsService : IDisposable
    {
        public SystemMediaTransportControls SystemMediaControls { get; private set; }
        private readonly SystemMediaTransportControlsTimelineProperties _timelineProperties = new();
        private MediaPlayer mediaPlayer;
        private ILogger<SystemMediaControlsService> _logger;
        public SystemMediaControlsService(ILogger<SystemMediaControlsService> logger)
        {
            _logger = logger;
        }

        private PlaybackCommands? _commands;
        private bool _stopping;
        private long _mediaVersion;
        private Task _mediaUpdate = Task.CompletedTask;
        private CancellationTokenSource? _mediaUpdateCts;
        private readonly object _mediaUpdateGate = new();
        private InMemoryRandomAccessStream? _thumbnailStream;
        public async Task StopAsync()
        {
            Task pending;
            CancellationTokenSource? updateCts;
            lock (_mediaUpdateGate)
            {
                _stopping = true;
                _mediaVersion++;
                updateCts = _mediaUpdateCts;
                _mediaUpdateCts = null;
                pending = _mediaUpdate;
            }
            updateCts?.Cancel();
            updateCts?.Dispose();
            if (SystemMediaControls is not null) SystemMediaControls.IsEnabled = false;
            await pending;
        }
        public void Initialize(PlaybackCommands commands)
        {
            if (_stopping || mediaPlayer is not null) return;
            _commands = commands;
            commands.ToggleCommand.CanExecuteChanged += CommandsChanged;
            commands.NextCommand.CanExecuteChanged += CommandsChanged;
            InitializeSystemMediaTransportControls();
        }
        private void InitializeSystemMediaTransportControls()
        {
            try
            {
                mediaPlayer = new MediaPlayer();
                SystemMediaControls = mediaPlayer.SystemMediaTransportControls;
                mediaPlayer.CommandManager.IsEnabled = false;
                if (SystemMediaControls is not null)
                {
                    SystemMediaControls.IsEnabled = false;
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

        public void EnableControls()
        {
            CommandsChanged(this, EventArgs.Empty);
            if (SystemMediaControls is not null) SystemMediaControls.IsEnabled = true;
        }

        private void CommandsChanged(object? sender, EventArgs e)
        {
            if (SystemMediaControls is null || _commands is null) return;
            SystemMediaControls.IsPlayEnabled = _commands.PlayCommand.CanExecute(null);
            SystemMediaControls.IsPauseEnabled = _commands.PauseCommand.CanExecute(null);
            SystemMediaControls.IsNextEnabled = _commands.NextCommand.CanExecute(null);
            SystemMediaControls.IsPreviousEnabled = _commands.PreviousCommand.CanExecute(null);
        }

        // 处理播放位置更改请求
        private void SystemMediaControls_PlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        {
            long position = (long)args.RequestedPlaybackPosition.TotalMilliseconds;
            App.MainWindow.DispatcherQueue.TryEnqueue(() => _commands?.SeekCommand.Execute(position));
        }

        private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            var button = args.Button;
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play: _commands?.PlayCommand.Execute(null); break;
                    case SystemMediaTransportControlsButton.Pause: _commands?.PauseCommand.Execute(null); break;
                    case SystemMediaTransportControlsButton.Next: _commands?.NextCommand.Execute(null); break;
                    case SystemMediaTransportControlsButton.Previous: _commands?.PreviousCommand.Execute(null); break;
                }
            });
        }

        public void Dispose()
        {
            CancellationTokenSource? updateCts;
            lock (_mediaUpdateGate)
            {
                _stopping = true;
                _mediaVersion++;
                updateCts = _mediaUpdateCts;
                _mediaUpdateCts = null;
            }
            updateCts?.Cancel();
            updateCts?.Dispose();
            _thumbnailStream?.Dispose();
            _thumbnailStream = null;
            if (_commands is not null)
            {
                _commands.ToggleCommand.CanExecuteChanged -= CommandsChanged;
                _commands.NextCommand.CanExecuteChanged -= CommandsChanged;
            }
            _commands = null;
            if (SystemMediaControls is not null)
            {
                SystemMediaControls.IsEnabled = false;
                SystemMediaControls.ButtonPressed -= SystemMediaControls_ButtonPressed;
                SystemMediaControls.PlaybackPositionChangeRequested -= SystemMediaControls_PlaybackPositionChangeRequested;
            }
            mediaPlayer?.Dispose();
            mediaPlayer = null;
            SystemMediaControls = null;
        }

        public void UpdateSystemMediaControlsState()
        {
            if (SystemMediaControls is null) return;
            SystemMediaControls.PlaybackStatus = AppData.IsPlaying ?
                MediaPlaybackStatus.Playing :
                MediaPlaybackStatus.Paused;
        }


        public Task UpdateMediaInfo(string title, string artist, string album, byte[] cover = null)
        {
            var updateCts = new CancellationTokenSource();
            CancellationTokenSource? previousCts;
            Task update;
            lock (_mediaUpdateGate)
            {
                if (_stopping || SystemMediaControls is null)
                {
                    updateCts.Dispose();
                    return Task.CompletedTask;
                }

                previousCts = _mediaUpdateCts;
                _mediaUpdateCts = updateCts;
                update = UpdateMediaInfoCoreAsync(++_mediaVersion, title, artist, album, cover, updateCts);
                _mediaUpdate = update;
            }

            // SMTC 只需要最新一首歌。取消并释放前一次更新，避免大封面
            // 被串行任务链保留到所有旧更新完成后才可回收。
            previousCts?.Cancel();
            previousCts?.Dispose();
            return update;
        }

        private async Task UpdateMediaInfoCoreAsync(long version, string title, string artist, string album, byte[]? cover, CancellationTokenSource ownerCts)
        {
            InMemoryRandomAccessStream? stream = null;
            var token = ownerCts.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (_stopping || version != _mediaVersion || SystemMediaControls is null) return;
                RandomAccessStreamReference thumbnail;
                if (cover is { Length: > 0 })
                {
                    stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(cover.AsBuffer());
                    token.ThrowIfCancellationRequested();
                    stream.Seek(0);
                    thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                }
                else thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri("ms-appx:///Assets/Album.png"));
                token.ThrowIfCancellationRequested();
                if (_stopping || version != _mediaVersion || SystemMediaControls is null) return;
                var updater = SystemMediaControls.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = artist;
                updater.MusicProperties.AlbumTitle = album;
                updater.Thumbnail = thumbnail;
                updater.Update();
                var oldStream = _thumbnailStream;
                _thumbnailStream = stream;
                stream = null;
                oldStream?.Dispose();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "更新 SMTC 媒体信息失败"); }
            finally
            {
                stream?.Dispose();
                if (ReferenceEquals(Interlocked.CompareExchange(ref _mediaUpdateCts, null, ownerCts), ownerCts))
                    ownerCts.Dispose();
            }
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

    }
}
