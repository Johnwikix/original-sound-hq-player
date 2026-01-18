using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace WinUIMusicPlayer.Model
{
    public partial class LyricLine : ObservableObject
    {
        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }
        private string _tranLateText = string.Empty;
        public string TransLateText
        {
            get => _tranLateText;
            set => SetProperty(ref _tranLateText, value);
        }

        private bool _isCurrent = false;
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }
        private TimeSpan _time = TimeSpan.Zero;
        public TimeSpan Time { 
            get => _time;
            set => SetProperty(ref _time, value);
        }
        private TimeSpan _lineAnimateDuration = TimeSpan.Zero;
        public TimeSpan LineAnimateDuration { 
            get => _lineAnimateDuration; 
            set => SetProperty(ref _lineAnimateDuration, value); 
        }

        public double DynamicLyricsSize(bool isEnable, double fontsize, bool IsLyricstype)
        {
            if (isEnable)
            {
                return fontsize;                
            }
            else {
                if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 1440)
                {
                    return IsLyricstype ? 28.0 : 22.0;
                }
                if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 1920)
                {
                    return IsLyricstype ? 32.0 : 26.0;
                }
                if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 2160)
                {
                    return IsLyricstype ? 36.0 : 30.0;
                }
                if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 2560)
                {
                    return IsLyricstype ? 40.0 : 34.0;
                }
                return IsLyricstype ? 44.0 : 38.0;
            }           
        }

    }
}
