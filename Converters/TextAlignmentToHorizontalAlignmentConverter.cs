using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class TextAlignmentToHorizontalAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TextAlignment textAlignment)
            {
                switch (textAlignment)
                {
                    case TextAlignment.Left:
                        return HorizontalAlignment.Left;
                    case TextAlignment.Center:
                        return HorizontalAlignment.Center;
                    case TextAlignment.Right:
                        return HorizontalAlignment.Right;
                    case TextAlignment.Justify:
                        return HorizontalAlignment.Left;
                    case TextAlignment.DetectFromContent:
                        return HorizontalAlignment.Left;
                }
            }
            return HorizontalAlignment.Center;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
