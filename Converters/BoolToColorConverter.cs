using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace WinUIMusicPlayer.Converters
{
    // 转换器：当前行高亮颜色
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            Color accentColor = GetAccentColor();
            return (bool)value ? new SolidColorBrush(accentColor) : new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private Color GetAccentColor()
        {
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemAccentColor", out object accentResource))
            {
                if (accentResource is Color accentColor)
                {
                    return accentColor;
                }
            }
            return Colors.Black;
        }
    }
}
