using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class PlayingDetailViewModel:ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public double TitleFontSize { get; set => SetProperty(ref field, value); } = 24;
        public double InfoFontSize { get; set => SetProperty(ref field, value); } = 12;
        public PlayingDetailViewModel(AppViewModel appViewModel)
        {
            AppViewModel = appViewModel;
            AppViewModel.UpdateCover();
        }

        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            if (App.MainWindow.AppWindow is not null)
            {
                if (AppViewModel.IsFullScreen)
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                }
                else
                {
                    App.MainWindow.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                AppViewModel.IsFullScreen = !AppViewModel.IsFullScreen;
            }

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
        private void PlayDetailButtonVisibleChanged()
        {
            AppViewModel.IsPlayDetailButtonVisible = !AppViewModel.IsPlayDetailButtonVisible;
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
                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(AppViewModel.CurrentPlayingList[index - 1]);
            }
            else if (index == 0 && AppViewModel.CurrentPlayingList.Count > 1)
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(AppViewModel.CurrentPlayingList[^1]);

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
                    TimeSpan currentTime = AppViewModel.UILyrics[index].Time;
                    TimeSpan nextTime = AppViewModel.UILyrics[nextIndex].Time;
                    AppViewModel.LyricsDurationTime = nextTime.Subtract(currentTime);
                }
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    for (int i = 0; i < AppViewModel.UILyrics.Count; i++)
                    {
                        AppViewModel.UILyrics[i].IsCurrent = (i == index);
                    }
                    AppViewModel.LastLyricIndex = index;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新歌词失败: {ex.Message}");
                }
            });
        }
    }
}
