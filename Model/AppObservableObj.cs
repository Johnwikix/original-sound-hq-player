using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Text;
using WinUIMusicPlayer.Services;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class AppObservableObj : ObservableObject
    {
        public Music CurrentArtistObj {
            get;
            set => SetProperty(ref field, value);
        }
        public Music CurrentAlbumObj
        {
            get;
            set => SetProperty(ref field, value);
        }
        public Music CurrentFolderObj
        {
            get;
            set => SetProperty(ref field, value);
        }
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

        private string _playModeFlyoutText;
        public string PlayModeFlyoutText
        {
            get => _playModeFlyoutText;
            set => SetProperty(ref _playModeFlyoutText, value);
        }

        private ObservableCollection<Music> _sequentialPlayingList;
        public ObservableCollection<Music> SequentialPlayingList
        {
            get => _sequentialPlayingList;
            set
            {
                SetProperty(ref _sequentialPlayingList, value);
            }
        }

        private ObservableCollection<Music> _currentPlayingList;
        public ObservableCollection<Music> CurrentPlayingList
        {
            get => _currentPlayingList;
            set
            {
                SetProperty(ref _currentPlayingList, value);
            }
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

        private bool _isMuted = false;
        public bool IsMuted
        {
            get => _isMuted;
            set => SetProperty(ref _isMuted, value);
        }
        private double _tempVolume = 50;
        public double TempVolume
        {
            get => _tempVolume;
            set => SetProperty(ref _tempVolume, value);
        }
        private double _volume = 50;
        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    if (IsInitialized)
                    {
                        if (value > 0)
                        {
                            IsMuted = false;
                        }
                        if (!IsMuted)
                        {
                            TempVolume = value;
                        }
                        //AppData.Volume = (float)value / 100;
                        App.Services.GetRequiredService<BassPlayerCommandService>().SetVolume(value / 100);
                        //_musicPlaybackService.SetVolume(AppData.Volume);
                    }
                }
            }
        }

        private string _playTimeText = "00:00/00:00";
        public string PlayTimeText
        {
            get => _playTimeText;
            set => SetProperty(ref _playTimeText, value);
        }

        private double _progressSliderMax = 100;
        public double ProgressSliderMax
        {
            get => _progressSliderMax;
            set => SetProperty(ref _progressSliderMax, value);
        }

        private ObservableCollection<LyricLine> _uiLyrics = [];
        public ObservableCollection<LyricLine> UILyrics
        {
            get => _uiLyrics;
            set => SetProperty(ref _uiLyrics, value);
        }
        private int _lastLyricIndex = -1;
        public int LastLyricIndex
        {
            get => _lastLyricIndex;
            set => SetProperty(ref _lastLyricIndex, value);
        }

        private Thickness _lyricsMargin;
        public Thickness LyricsMargin
        {
            get => _lyricsMargin;
            set {
                if (SetProperty(ref _lyricsMargin, value)) {
                    _ = _musicDatabaseService.SaveSettingAsync();
                }
            }
        }

        private bool _isPlayDetailButtonVisible = true;
        public bool IsPlayDetailButtonVisible
        {
            get => _isPlayDetailButtonVisible;
            set
            {
                if (SetProperty(ref _isPlayDetailButtonVisible, value))
                {
                    //AppSettings.IsPlayDetailBtnVisible = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private ImageSource? _lyricPageBackgroundSource = null;
        public ImageSource? LyricPageBackgroundSource
        {
            get => _lyricPageBackgroundSource;
            set => SetProperty(ref _lyricPageBackgroundSource, value);
        }

        private bool _isInitialized = false;
        public bool IsInitialized
        {
            get => _isInitialized;
            set => SetProperty(ref _isInitialized, value);
        }

        private Visibility _usbDeviceVisibility = Visibility.Collapsed;
        public Visibility UsbDeviceVisibility
        {
            get => _usbDeviceVisibility;
            set => SetProperty(ref _usbDeviceVisibility, value);
        }
        private ObservableCollection<UsbStorageDevice> _usbStorageDevices;
        public ObservableCollection<UsbStorageDevice> UsbStorageDevices
        {
            get => _usbStorageDevices;
            set => SetProperty(ref _usbStorageDevices, value);
        }
        private int _usbSelectedIndex = 0;
        public int UsbSelectedIndex
        {
            get => _usbSelectedIndex;
            set => SetProperty(ref _usbSelectedIndex, value);
        }
        private Visibility _processRingVisibility = Visibility.Collapsed;
        public Visibility ProcessRingVisibility
        {
            get => _processRingVisibility;
            set => SetProperty(ref _processRingVisibility, value);
        }
        private bool _isFullScreen = false;
        public bool IsFullScreen
        {
            get => _isFullScreen;
            set => SetProperty(ref _isFullScreen, value);
        }
        private string _infoBarTitle = string.Empty;
        public string InfoBarTitle
        {
            get => _infoBarTitle;
            set => SetProperty(ref _infoBarTitle, value);
        }
        private bool _infoBarIsOpen = false;
        public bool InfoBarIsOpen
        {
            get => _infoBarIsOpen;
            set => SetProperty(ref _infoBarIsOpen, value);
        }
        private string _infoBarMessage = string.Empty;
        public string InfoBarMessage
        {
            get => _infoBarMessage;
            set => SetProperty(ref _infoBarMessage, value);
        }
        private string _pageType = string.Empty;
        public string PageType
        {
            get => _pageType;
            set => SetProperty(ref _pageType, value);
        }
        private bool _isInPlayingDetailMode = false;
        public bool IsInPlayingDetailMode
        {
            get => _isInPlayingDetailMode;
            set => SetProperty(ref _isInPlayingDetailMode, value);
        }
        private bool _isAcrylicBrushOpacity = false;
        public bool IsAcrylicBrushOpacity
        {
            get => _isAcrylicBrushOpacity;
            set => SetProperty(ref _isAcrylicBrushOpacity, value);
        }

        private float _topControlsOpacity = 1.0f;
        public float TopControlsOpacity
        {
            get => _topControlsOpacity;
            set => SetProperty(ref _topControlsOpacity, value);
        }
        private MusicDatabaseService _musicDatabaseService { get; }

        public AppObservableObj(MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;
            //UILyrics = new ObservableCollection<LyricLine>();
        }
    }
}
