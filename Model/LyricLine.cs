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

        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }

        public TimeSpan Time { get; set; }

    }
}
