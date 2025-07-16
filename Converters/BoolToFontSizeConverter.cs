using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    // 转换器：当前行字体大小
    public class BoolToFontSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (App.MainWindow.AppWindow.Size.Width <= 1920) {
                return (bool)value ? 28.0 : 19.0;
            }
            if(App.MainWindow.AppWindow.Size.Width <= 2160) {
                return (bool)value ? 32.0 : 23.0;
            }
            if (App.MainWindow.AppWindow.Size.Width <= 2560) {
                return (bool)value ? 36.0 : 27.0;
            }
            return (bool)value ? 40.0 : 31.0;

        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
