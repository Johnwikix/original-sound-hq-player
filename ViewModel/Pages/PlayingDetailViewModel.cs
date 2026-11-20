using AnimatedWin2dControls.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class PlayingDetailViewModel : ObservableObject
    {
        public PlaybackCommands Playback { get; }
        public AppViewModel AppViewModel { get; }
        public WinUIMusicPlayer.State.AppState State => AppViewModel.State;
        public double TitleFontSize { get; set => SetProperty(ref field, value); } = 24;
        public double ArtistAlbumFontSize { get => field; set => SetProperty(ref field, value); } = 22;
        public double InfoFontSize { get; set => SetProperty(ref field, value); } = 12;
        private ILogger<PlayingDetailViewModel> _logger;
        private readonly PlaybackSpectrumSource _spectrumSource;

        public PlayingDetailViewModel(AppViewModel appViewModel, ILogger<PlayingDetailViewModel> logger,
            PlaybackCommands playback, BassPlayerCommandService player)
        {
            _spectrumSource = new(player);
            Playback = playback;
            AppViewModel = appViewModel;
            _logger = logger;
            AppViewModel.UpdateCover();
        }

        public IPlaybackSpectrumSource? GetSpectrumSource(Music? track)
            => track is { IsRemote: false } ? _spectrumSource : null;

        [RelayCommand]
        public void OnPlayButtonChanged()
        {
            PlayButton_Click();
        }

        public void PlayButton_Click()
        {
            // IsPlaying's setter triggers the taskbar/SMTC update automatically, so no
            // manual UpdatePlayPauseButtonIcon here (it would run with stale state).
            Playback.ToggleCommand.Execute(null);
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (AppViewModel.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RepeatOff;
                    break;
                case PlayMode.RepeatOff:
                    AppViewModel.CurrentPlayMode = PlayMode.SingleLoop;
                    break;
            }
            App.Services.GetRequiredService<BassPlayerCommandService>().UpdateSettings();
        }

        [RelayCommand]
        public void OnNextMusicButtonChanged()
        {
            NextMusicButton_Click();
        }

        [RelayCommand]
        public void OnLastMusicButtonChanged()
        {
            LastMusicButton_Click();
        }

        public void NextMusicButton_Click()
        {
            Playback.NextCommand.Execute(null);
        }

        public void LastMusicButton_Click()
        {
            PlayLastTrack();
        }

        private void PlayLastTrack() => Playback.PreviousCommand.Execute(null);

        public void UpdateLyricsToUI(int index)
        {
            if (AppViewModel.LastLyricIndex == index)
                return;
            var uiLyrics = AppViewModel.UILyrics;
            if (index >= 0 && index < uiLyrics.Count - 1)
            {
                var currentTime = TimeSpan.FromMilliseconds(uiLyrics[index].StartMs);
                var nextTime = TimeSpan.FromMilliseconds(uiLyrics[index + 1].StartMs);
                AppViewModel.LyricsDurationTime = nextTime - currentTime;
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(uiLyrics, AppViewModel.UILyrics)) return;
                try
                {
                    for (int i = 0; i < uiLyrics.Count; i++)
                    {
                        uiLyrics[i].IsCurrent = (i == index);
                    }
                    AppViewModel.LastLyricIndex = index;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"更新歌词失败: {ex.Message}");
                }
            });
        }
    }
}
