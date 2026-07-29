using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public partial class TextAlignmentToHorizontalAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TextAlignment alignment)
            {
                return alignment switch
                {
                    TextAlignment.Center => HorizontalAlignment.Center,
                    TextAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
            }
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
