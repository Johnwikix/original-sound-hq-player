using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class MusicBrowseViewModel : ObservableObject
    {
        private PlayMode _currentPlayMode = PlayMode.ListLoop;
        public PlayMode CurrentPlayMode
        {
            get => _currentPlayMode;
            set => SetProperty(ref _currentPlayMode, value);
        }

        private Music _currentPlayingMusic;
        public Music CurrentPlayingMusic
        {
            get => _currentPlayingMusic;
            set => SetProperty(ref _currentPlayingMusic, value);
        }
        private string _musicInfo;
        public string MusicInfo
        {
            get => _musicInfo;
            set => SetProperty(ref _musicInfo, value);
        }

        public BitmapImage _musicDetailCover;
        public BitmapImage MusicDetailCover
        {
            get => _musicDetailCover;
            set => SetProperty(ref _musicDetailCover, value);
        }

        private int _volume = 50;
        public int Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                }
            }
        }

        private bool _isPlaying = false;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private MainWindow _mainWindow;

        public MusicBrowseViewModel()
        {
            // Initialize any necessary properties or services here
            // For example, you might want to set up a service for managing music playback
            _mainWindow = App.MainWindow;
            CurrentPlayMode = AppData.PlayMode;
        }
        [RelayCommand]
        public void OnPlayModeChanged()
        {
            switch (CurrentPlayMode)
            {
                case PlayMode.SingleLoop:
                    AppData.PlayMode = PlayMode.ListLoop;
                    CurrentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    AppData.PlayMode = PlayMode.RandomLoop;
                    CurrentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    AppData.PlayMode = PlayMode.RepeatOff;
                    CurrentPlayMode = PlayMode.RepeatOff;
                    break;
                case PlayMode.RepeatOff:
                    AppData.PlayMode = PlayMode.SingleLoop;
                    CurrentPlayMode = PlayMode.SingleLoop;
                    break;
            }
            _mainWindow.UpdateAppNotifyIconControl();
        }
    }
}
