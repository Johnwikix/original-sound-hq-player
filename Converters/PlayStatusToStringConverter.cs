using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public partial class PlayStatusToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool IsPlaying)
            {
                if (IsPlaying)
                {
                    return ToolUtils.GetString("IconPause");
                }
                else
                {
                    return ToolUtils.GetString("IconPlay");
                }
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
