using Microsoft.UI.Xaml.Data;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Converters
{
    // 转换器：当前行字体大小
    public class BoolToFontSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 1440)
            {
                return (bool)value ? 28.0 : 22.0;
            }
            if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 1920)
            {
                return (bool)value ? 32.0 : 22.0;
            }
            if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 2160)
            {
                return (bool)value ? 34.0 : 22.0;
            }
            if ((App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale) <= 2560)
            {
                return (bool)value ? 36.0 : 22.0;
            }
            return (bool)value ? 40.0 : 22.0;

        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
