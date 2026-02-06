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

    }
}
