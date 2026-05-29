using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class ThicknessToDoubleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Microsoft.UI.Xaml.Thickness thickness)
            {
                return thickness.Left;
            }
            return 20.0;
        }

        // Double -> Thickness (通常用于从 Slider 动态调整控件边距)
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is double doubleValue)
            {
                return new Microsoft.UI.Xaml.Thickness(doubleValue, 0, doubleValue, 0);
            }
            return new Microsoft.UI.Xaml.Thickness(20, 0, 20, 0);
        }
    }
}
