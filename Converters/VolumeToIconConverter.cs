using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public partial class VolumeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double volume)
            {
                if (volume > 75)
                {
                    return "\ue995";
                }
                else if (volume > 50)
                {
                    return "\ue994";
                }
                else if (volume > 25)
                {
                    return "\ue993";
                }
                else if (volume > 0)
                {
                    return "\uE992";
                }
                else if (volume <= 0)
                {
                    return "\ue74f";
                }
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
