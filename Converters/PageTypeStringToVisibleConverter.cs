using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class PageTypeStringToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string pageType)
            {
                if (pageType == "artistBrowse")
                {
                    return Visibility.Collapsed;
                }
                if (pageType == "folderBrowse")
                {
                    return Visibility.Collapsed;
                }
                if (pageType == "playlistBrowse")
                {
                    return Visibility.Collapsed;
                }
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
