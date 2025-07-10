using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class SongCollectionPageTypeToRadiusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string pageType)
            {
                if (pageType == "artist")
                {
                    return new CornerRadius(75);
                }
                if (pageType == "folder")
                {
                    return new CornerRadius(10);
                }
            }
            return new CornerRadius(5);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
