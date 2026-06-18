using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace WinUIMusicPlayer.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        private static SolidColorBrush? s_whiteBrush;
        private static SolidColorBrush? s_accentBrush;
        private static Color s_lastAccentColor;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            s_whiteBrush ??= new SolidColorBrush(Colors.White);
            Color accentColor = GetAccentColor();
            if (s_accentBrush is null || s_lastAccentColor != accentColor)
            {
                s_lastAccentColor = accentColor;
                s_accentBrush = new SolidColorBrush(accentColor);
            }
            return (bool)value ? s_accentBrush : s_whiteBrush;
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
