using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class BoolToOpacityConverter : IValueConverter
    {
        // 转换器：当前行透明度
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (bool)value ? 1.0 : 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
