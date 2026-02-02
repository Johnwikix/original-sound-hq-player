using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using Windows.UI;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class AppObservableObj : ObservableObject
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
                    if (IsInitialized)
                    {
                        //AppSettings.IsPlayDetailBtnVisible = value;
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

        private int _coverSize = 150;
        public int CoverSize
        {
            get => _coverSize;
            set
            {
                if (SetProperty(ref _coverSize, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _dsdGain = 6;
        public int DsdGain
        {
            get => _dsdGain;
            set
            {
                if (SetProperty(ref _dsdGain, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.dsdGain = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }
        private bool _isAutoLyricsEnabled = true;
        public bool IsAutoLyricsEnabled
        {
            get => _isAutoLyricsEnabled;
            set
            {
                if (SetProperty(ref _isAutoLyricsEnabled, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.isAutoLyricsEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isRunningBackend = true;
        public bool IsRunningBackend
        {
            get => _isRunningBackend;
            set => SetProperty(ref _isRunningBackend, value);
        }

        private int _latency = 300;
        public int Latency
        {
            get => _latency;
            set
            {
                if (SetProperty(ref _latency, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.Latency = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isCustomAppSize = false;
        public bool IsCustomAppSize
        {
            get => _isCustomAppSize;
            set
            {
                if (SetProperty(ref _isCustomAppSize, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsCustomAppSize = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _appWidth = 1440;
        public int AppWidth
        {
            get => _appWidth;
            set
            {
                if (SetProperty(ref _appWidth, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.AppWidth = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _appHeight = 810;
        public int AppHeight
        {
            get => _appHeight;
            set
            {
                if (SetProperty(ref _appHeight, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.AppHeight = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isCoverCacheEnabled = false;
        public bool IsCoverCacheEnabled
        {
            get => _isCoverCacheEnabled;
            set
            {
                if (SetProperty(ref _isCoverCacheEnabled, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.isCoverCacheEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _defaultEntryComboBoxTag = "AddFolder";
        public string DefaultEntryComboBoxTag
        {
            get => _defaultEntryComboBoxTag;
            set
            {
                if (SetProperty(ref _defaultEntryComboBoxTag, value))
                {
                    //AppSettings.DefualtEntry = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _defaultPlayListComboBoxTag = "song";
        public string DefaultPlayListComboBoxTag
        {
            get => _defaultPlayListComboBoxTag;
            set
            {
                if (SetProperty(ref _defaultPlayListComboBoxTag, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.DefualtPlayList = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        

        private string _backdropType = "TransparentAcrylic";

        public string BackdropType
        {
            get => _backdropType;
            set
            {
                if (SetProperty(ref _backdropType, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.AppStyle = value;               
                        App.MainWindow?.SetAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _themeType = "Dark"; // 默认值

        public string ThemeType
        {
            get => _themeType;
            set
            {
                if (SetProperty(ref _themeType, value))
                {
                    // 保存设置                   
                    if (IsInitialized)
                    {
                        //AppSettings.AppTheme = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private int _entranceAnimationTime;
        public int EntranceAnimationTime
        {
            get => _entranceAnimationTime;
            set
            {
                if (SetProperty(ref _entranceAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.EntranceAnimationTime = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _slideAnimationTime;
        public int SlideAnimationTime
        {
            get => _slideAnimationTime;
            set
            {
                if (SetProperty(ref _slideAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.SlideAnimationTime = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _drillInAnimationTime;
        public int DrillInAnimationTime
        {
            get => _drillInAnimationTime;
            set
            {
                if (SetProperty(ref _drillInAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.DrillInAnimationTime = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isIsBackgroundCoverEnabled = false;
        public bool IsBackgroundCoverEnabled
        {
            get => _isIsBackgroundCoverEnabled;
            set
            {
                if (SetProperty(ref _isIsBackgroundCoverEnabled, value))
                {
                    AppSettings.IsBackgroundCoverEnabled = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                        App.Services.GetRequiredService<MusicBrowsePage>()?.ChangeAcrylicBrushBackgroundOpacity();
                    }
                }
            }
        }

        private string _version = string.Empty;
        public string Version
        {
            get => _version;
            set => SetProperty(ref _version, value);
        }

        private bool _isFolderWatchEnabled = true;
        public bool IsFolderWatchEnabled
        {
            get => _isFolderWatchEnabled;
            set
            {
                if (SetProperty(ref _isFolderWatchEnabled, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsFolderWatchEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private ObservableCollection<FontInfo> _fontFamilyList;
        public ObservableCollection<FontInfo> FontFamilyList
        {
            get => _fontFamilyList;
            set => SetProperty(ref _fontFamilyList, value);
        }
        private FontInfo _fontFamily;
        public FontInfo FontFamily
        {
            get => _fontFamily;
            set
            {
                if (SetProperty(ref _fontFamily, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.GlobalFont = value.FontFamily;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private Color _customColor = Color.FromArgb(255, 128, 128, 128);
        public Color CustomColor
        {
            get => _customColor;
            set
            {
                if (SetProperty(ref _customColor, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.CustomColorAlpha = value.A;
                        //AppSettings.CustomColorRed = value.R;
                        //AppSettings.CustomColorGreen = value.G;
                        //AppSettings.CustomColorBlue = value.B;
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private float _customOpacity = 50f;
        public float CustomOpacity
        {
            get => _customOpacity;
            set
            {
                if (SetProperty(ref _customOpacity, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.CustomAcrylicOpacity = value / 100;
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isUpdateBackDrop = false;
        public bool IsUpdateBackDrop
        {
            get => _isUpdateBackDrop;
            set
            {
                if (SetProperty(ref _isUpdateBackDrop, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsUpdateBackDrop = value;
                        App.MainWindow?.UpdateBackdropActiveState(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private string _lyricsAlignment = "Left";
        public string LyricsAlignment
        {
            get => _lyricsAlignment;
            set
            {
                if (SetProperty(ref _lyricsAlignment, value))
                {
                    AppSettings.LyricsAlignment = ConvertStringToTextAlignment(value);
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isGlobalFontSizeEnabled = false;
        public bool IsGlobalFontSizeEnabled
        {
            get => _isGlobalFontSizeEnabled;
            set
            {
                if (SetProperty(ref _isGlobalFontSizeEnabled, value))
                {
                    AppSettings.IsGlobalFontSizeEnabled = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private double _globalFontSize = 32f;
        public double GlobalFontSize
        {
            get => _globalFontSize;
            set
            {
                if (SetProperty(ref _globalFontSize, value))
                {
                    AppSettings.GlobalFontSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _musicCoverCache;
        public string MusicCoverCache
        {
            get => _musicCoverCache;
            set {
                if (SetProperty(ref _musicCoverCache, value)) {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isDopEnabled;
        public bool IsDopEnabled
        {
            get => _isDopEnabled;
            set
            {
                if (SetProperty(ref _isDopEnabled, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsDopEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private bool _isFadeEnabled;
        public bool IsFadeEnabled
        {
            get => _isFadeEnabled;
            set
            {
                if (SetProperty(ref _isFadeEnabled, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsFadeEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private string _dsdPcmFreq = "88200";
        public string DsdPcmFreq
        {
            get => _dsdPcmFreq;
            set
            {
                if (SetProperty(ref _dsdPcmFreq, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.dsdPcmFreq = int.Parse(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private bool _isWFWLyrics;
        public bool IsWFWLyrics
        {
            get => _isWFWLyrics;
            set
            {
                if (SetProperty(ref _isWFWLyrics, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.IsWFWLyrics = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private double _lyricsBlurAmount;
        public double LyricsBlurAmount
        {
            get => _lyricsBlurAmount;
            set
            {
                if (SetProperty(ref _lyricsBlurAmount, value))
                {
                    AppSettings.LyricsBlurAmount = value / 10;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private MusicDatabaseService _musicDatabaseService { get; }

        public AppObservableObj(MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;
            //UILyrics = new ObservableCollection<LyricLine>();
        }
    }
}
