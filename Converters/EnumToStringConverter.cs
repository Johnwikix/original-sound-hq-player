using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public partial class EnumToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value?.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string s && targetType != null && targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, s, out object result))
                {
                    return result;
                }
            }
            return value;
        }
    }
}
