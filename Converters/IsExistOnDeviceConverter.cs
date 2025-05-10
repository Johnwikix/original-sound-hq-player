using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class IsExistOnDeviceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if ((int)value == 1)
            {
                return "\uE73A";
            }
            if ((int)value == 2)
            {
                return "\uE73D";
            }
            return "";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
