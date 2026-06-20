using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public partial class PlayStatusToStringConverter : IValueConverter
    {
        private static readonly string IconPlayText = ToolUtils.GetString("IconPlay");
        private static readonly string IconPauseText = ToolUtils.GetString("IconPause");

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool IsPlaying)
            {
                return IsPlaying ? IconPauseText : IconPlayText;
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
