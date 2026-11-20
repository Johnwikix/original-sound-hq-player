using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

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
            else if (typeId == MessageTypeId.PlayEnded && PlaybackEnded.TryRead(payload.Span, out var ended))
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!CanReceive || _remote.IsActive) return;
                    var coordinator = App.Services.GetRequiredService<PlaybackCoordinator>();
                    if (!coordinator.TryAcceptLocalEnd(ended)) return;
                    AppViewModel.IsPlaying = false;
                    AppViewModel.StopProgressTimer();
                    var (_, total) = AppViewModel.GetTimeProgressCache();
                    AppViewModel.MarkPlaybackEnded(total);
                    AppViewModel.UpdateProgressTimerUI();
                    _ = coordinator.AdvanceAfterEndAsync();
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

        public Task AutoPlayNextTrack() => CanPlay
            ? App.Services.GetRequiredService<PlaybackCoordinator>().AdvanceAfterEndAsync()
            : Task.CompletedTask;

        public void MusicEnd(bool preserveInterruptedProgress = false)
        {
            App.Services.GetRequiredService<PlaybackCoordinator>().CancelManualNext();
            _ = _remote.StopAsync(preserveFailedProgress: preserveInterruptedProgress);
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
                if (preserveInterruptedProgress &&
                    (AppViewModel.CurrentPlayingMusic?.IsRemote != true || !_remote.NeedsStart)) return;
                AppViewModel.StopProgressTimer();
                if (!preserveInterruptedProgress) AppViewModel.ProgressSlider = 0;
                AppViewModel.IsPlaying = false;
                if (!preserveInterruptedProgress) AppViewModel.RemotePlaybackStatus = "";
            });
        }

        public void PlayNextTrack()
        {
            if (!CanPlay || AppViewModel.CurrentPlayingList.Count == 0) return;
            try
            {
                _ = App.Services.GetRequiredService<PlaybackCoordinator>().PlayNextAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, $"PlayNextTrack failed: {ex.Message}"); }
        }

        public void PlayMusic(Music music, long playbackId)
        {
            if (!CanPlay) return;
            IpcService.Play(music.Path, playbackId);
            AppViewModel.StartProgressTimer();
        }

        public async Task PlayButton()
        {
            if (!CanPlay) return;
            if (AppViewModel.CurrentPlayingMusic?.IsRemote == true)
            {
                if (_remote.NeedsStart) await App.Services.GetRequiredService<PlaybackCoordinator>().ResumeCurrentAsync();
                else await _remote.SetIntentAsync(!_remote.WantsPlay);
                return;
            }
            try
            {
                var coordinator = App.Services.GetRequiredService<PlaybackCoordinator>();
                long stopIntent = coordinator.StopIntentVersion;
                if (AppViewModel.IsPlaying) coordinator.CancelManualNext();
                bool? state = await IpcService.PlayButton();
                if (state is bool s)
                {
                    // 完成任务前发布确认状态，共享命令才能正确处理在途的 Play/Pause 意图。
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        if (!CanPlay) return;
                        if (s) coordinator.ResumeAfterStop(stopIntent);
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
            if (!AppViewModel.CanSeekDisplayedPlayback) return;
            if (_remote.IsActive) { _ = _remote.SeekAsync(positionMs); return; }
            App.Services.GetRequiredService<PlaybackCoordinator>().CancelManualNext();
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

        internal void SetFftEnabled(FftConsumer consumer, bool enabled) => IpcService.SetFftEnabled(consumer, enabled);
        internal FftSnapshot? ReadFftSnapshot(out long receivedAtMs) => IpcService.ReadFftSnapshot(out receivedAtMs);

        /// <summary>Latest FFT frame for a visualizer; the snapshot is immutable after IPC parsing.</summary>
        public FftSnapshot? CurrentFftSnapshot => IpcService.CurrentFftSnapshot;

        /// <summary>获取用于持久化与设置传输的稳定 WASAPI 端点 ID。</summary>
        public string? GetWasapiEndpointId(int id) => IpcService.GetWasapiEndpointId(id);

        public Task<List<(int id, string name)>> GetWasapiDevices()
            => IpcService.GetWasapiDevices();

        public Task<List<(int id, string name)>> GetAsioDevices()
            => IpcService.GetAsioDevices();
    }
}
