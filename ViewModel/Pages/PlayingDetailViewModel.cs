using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class PlayingDetailViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public double TitleFontSize { get; set => SetProperty(ref field, value); } = 24;
        public double ArtistAlbumFontSize { get => field; set => SetProperty(ref field, value); } = 22;
        public double InfoFontSize { get; set => SetProperty(ref field, value); } = 12;
        private ILogger<PlayingDetailViewModel> _logger;
        public PlayingDetailViewModel(AppViewModel appViewModel, ILogger<PlayingDetailViewModel> logger)
        {
            AppViewModel = appViewModel;
            _logger = logger;
            AppViewModel.UpdateCover();
        }

        [RelayCommand]
        public void OnPlayButtonChanged()
        {
            PlayButton_Click();
        }

        public void PlayButton_Click()
        {
            App.Services.GetRequiredService<BassPlayerCommandService>().PlayButton();
            UpdatePlayPauseButtonIcon();
        }

        public void UpdatePlayPauseButtonIcon()
        {
            App.MainWindow.UpdateTaskbarIcon();
            App.Services.GetRequiredService<SystemMediaControlsService>().UpdateSystemMediaControlsState();
        }

        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (AppViewModel.CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.ListLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconListLoop");
                    break;
                case PlayMode.ListLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RandomLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconRandomLoop");
                    break;
                case PlayMode.RandomLoop:
                    AppViewModel.CurrentPlayMode = PlayMode.RepeatOff;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconSinglePlayback");
                    break;
                case PlayMode.RepeatOff:
                    AppViewModel.CurrentPlayMode = PlayMode.SingleLoop;
                    AppViewModel.PlayModeFlyoutText = ToolUtils.GetString("IconSingleTuneCirculation");
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
            App.Services.GetRequiredService<BassPlayerCommandService>().PlayNextTrack();
        }

        public void LastMusicButton_Click()
        {
            PlayLastTrack();
        }

        private void PlayLastTrack()
        {
            int index = AppViewModel.CurrentPlayingList.AsValueEnumerable()
                        .Select((music, i) => new { Music = music, Index = i })
                        .FirstOrDefault(x => x.Music.Id == AppViewModel.CurrentPlayingMusic.Id)
                        ?.Index ?? -1;
            if (index > 0)
            {
                _ = App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(AppViewModel.CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && AppViewModel.CurrentPlayingList.Count > 1)
            {
                _ = App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(AppViewModel.CurrentPlayingList[^1]);
            }
        }

        public void UpdateLyricsToUI(int index)
        {
            if (AppViewModel.LastLyricIndex == index)
                return;
            TimeSpan duration = TimeSpan.Zero;
            if (index >= 0 && index < AppViewModel.UILyrics.Count)
            {
                int nextIndex = index + 1;
                if (nextIndex < AppViewModel.UILyrics.Count)
                {
                    TimeSpan currentTime = TimeSpan.FromMilliseconds(AppViewModel.UILyrics[index].StartMs);
                    TimeSpan nextTime = TimeSpan.FromMilliseconds(AppViewModel.UILyrics[nextIndex].StartMs);
                    AppViewModel.LyricsDurationTime = nextTime.Subtract(currentTime);
                }
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var uiLyrics = AppViewModel.UILyrics;
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
