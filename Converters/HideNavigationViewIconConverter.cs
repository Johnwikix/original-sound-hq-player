using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class HideNavigationViewIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool HideNavigationViewButtonVisibility)
            {
                return HideNavigationViewButtonVisibility ? "\uED1A" : "\uE7B3";
            }
            return "\uE7B3";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
