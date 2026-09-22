using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services
{
    public class BassPlayerCommandService : IDisposable
    {
        public int? lastPlayedMusicId;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public AppViewModel AppViewModel { get; }
        private IpcService IpcService { get; set; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<BassPlayerCommandService> _logger;
        private readonly RemotePlaybackService _remote;
        private bool _disposed;
        private bool CanPlay => !_disposed && AppViewModel.CanStartPlayback;
        private bool CanReceive => !_disposed && AppViewModel.CanPublishState;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IpcService.NotificationReceived -= IpcService_NotificationReceived;
            _remote.Ended -= RemoteEnded;
        }

        public BassPlayerCommandService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<BassPlayerCommandService> logger, RemotePlaybackService remote)
        {
            IpcService = App.Services.GetRequiredService<IpcService>();
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            _remote = remote;
            _remote.Ended += RemoteEnded;
            IpcService.NotificationReceived += IpcService_NotificationReceived;
        }

        private void IpcService_NotificationReceived(MessageTypeId typeId, ReadOnlyMemory<byte> payload)
        {
            if (!CanReceive || App.MainWindow is null) return;
            if (_remote.IsActive && typeId is MessageTypeId.PlayState or MessageTypeId.PlayEnded) return;
            if (typeId == MessageTypeId.PlayState)
            {
                var state = BinarySerializer.ReadPlayStateResponse(payload.Span);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!CanReceive || _remote.IsActive) return;
                    AppViewModel.IsPlaying = state.IsPlaying;
                    if (state.IsPlaying)
                        AppViewModel.StartProgressTimer();
                    else
                        AppViewModel.StopProgressTimer();
                });
            }
            else if (typeId == MessageTypeId.PlayEnded)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!CanReceive || _remote.IsActive) return;
                    AppViewModel.IsPlaying = false;
                    AppViewModel.StopProgressTimer();
                    var (_, total) = AppViewModel.GetTimeProgressCache();
                    AppViewModel.MarkPlaybackEnded(total);
                    AppViewModel.UpdateProgressTimerUI();
                    _ = AutoPlayNextTrack();
                });
            }
            else if (typeId == MessageTypeId.VolumeWriteBack)
            {
                var vol = BinarySerializer.ReadVolumeResponse(payload.Span);
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!CanReceive) return;
                    AppViewModel.Volume = vol.Volume;
                });
            }
        }

        public void EqUpdate()
        {
            IpcService.UpdateEq();
        }

        /// <summary>Full EQ state sync awaiting the server's real applied state.</summary>
        public async Task<bool?> UpdateEqStateAsync()
        {
            return await IpcService.UpdateEqAsync();
        }

        public void UpdateSettings()
        {
            IpcService.UpdateSettings();
        }

        /// <summary>自动切歌单飞标记：防止重复 PlayEnded 通知导致并发触发两次切歌。</summary>
        private int _autoPlayInFlight;

        public async Task AutoPlayNextTrack()
        {
            if (!CanPlay) return;
            if (Interlocked.Exchange(ref _autoPlayInFlight, 1) != 0) return;
            try
            {
                AppViewModel.StopProgressTimer();
                switch (AppViewModel.CurrentPlayMode)
                {
                    case PlayMode.SingleLoop:
                        await MusicBrowsePlayMusic(AppViewModel.CurrentPlayingMusic);
                        break;
                    case PlayMode.ListLoop:
                    case PlayMode.RandomLoop:
                        // 一次性外部曲目不在列表内（index=-1）时 nextIndex=0，从播放队列第一首继续；
                        // 队列为空（如空库下直接打开外部文件）则结束播放，避免取模零异常。
                        var playingList = AppViewModel.CurrentPlayingList;
                        if (playingList.Count == 0)
                        {
                            MusicEnd();
                            break;
                        }
                        int currentIndex = AppViewModel.GetCurrentIndex();
                        int nextIndex = (currentIndex + 1) % playingList.Count;
                        await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAtAsync(nextIndex);
                        break;
                    case PlayMode.RepeatOff:
                        MusicEnd();
                        break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _autoPlayInFlight, 0);
            }
        }

        private static async Task MusicBrowsePlayMusic(Music music)
        {
            await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAsync(music);
        }

        public void MusicEnd()
        {
            _ = _remote.StopAsync();
            try
            {
                App.Services.GetService<PlaybackStatsService>()?.FlushSession();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "结算播放统计失败: {Message}", ex.Message);
            }
            IpcService.MusicEnd();
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanReceive) return;
                AppViewModel.StopProgressTimer();
                AppViewModel.ProgressSlider = 0;
                AppViewModel.IsPlaying = false;
                AppViewModel.RemotePlaybackStatus = "";
            });
        }

        public void PlayNextTrack()
        {
            if (!CanPlay || AppViewModel.CurrentPlayingList.Count == 0) return;
            try
            {
                int currentIndex = AppViewModel.GetCurrentIndex();
                int nextIndex = (currentIndex + 1) % AppViewModel.CurrentPlayingList.Count;
                _ = App.Services.GetRequiredService<PlaybackCoordinator>().PlayAtAsync(nextIndex);
            }
            catch (Exception ex) { _logger.LogError(ex, $"PlayNextTrack failed: {ex.Message}"); }
        }

        public void PlayMusic(Music music)
        {
            if (!CanPlay) return;
            IpcService.Play(music.Path);
            AppViewModel.StartProgressTimer();
        }

        public async Task PlayButton()
        {
            if (!CanPlay) return;
            if (AppViewModel.CurrentPlayingMusic?.IsRemote == true)
            {
                if (_remote.NeedsStart) await App.Services.GetRequiredService<PlaybackCoordinator>().PlayAsync(AppViewModel.CurrentPlayingMusic);
                else await _remote.SetIntentAsync(!_remote.WantsPlay);
                return;
            }
            try
            {
                bool? state = await IpcService.PlayButton();
                if (state is bool s)
                {
                    // 完成任务前发布确认状态，共享命令才能正确处理在途的 Play/Pause 意图。
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        if (!CanPlay) return;
                        AppViewModel.IsPlaying = s;
                        if (s) AppViewModel.StartProgressTimer();
                        else AppViewModel.StopProgressTimer();
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PlayButton failed");
            }
        }

        public void ChangeWaveChannelTime(long positionMs)
        {
            if (_remote.IsActive) { _ = _remote.SeekAsync(positionMs); return; }
            IpcService.SetPosition(positionMs);
        }

        private void RemoteEnded() { if (CanPlay) _ = AutoPlayNextTrack(); }

        public void SetVolume(double volume)
        {
            IpcService.ChangeVolume(volume);
        }

        public async Task<(long currentMs, long totalMs)?> GetTimeProgress()
        {
            try
            {
                return await IpcService.GetTimeProgress();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetTimeProgress failed: {ex.Message}");
                return null;
            }
        }

        public void ChangingSetting()
        {
            IpcService.UpdateSettings(true);
        }

        public void FadeOut()
        {
            IpcService.FadeOut();
        }

        /// <summary>获取用于持久化与设置传输的稳定 WASAPI 端点 ID。</summary>
        public string? GetWasapiEndpointId(int id) => IpcService.GetWasapiEndpointId(id);

        public Task<List<(int id, string name)>> GetWasapiDevices()
            => IpcService.GetWasapiDevices();

        public Task<List<(int id, string name)>> GetAsioDevices()
            => IpcService.GetAsioDevices();
    }
}
