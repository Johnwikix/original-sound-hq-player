using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Converters
{
    public class SongCollectionPageTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string pageType)
            {
                if (pageType =="album")
                {
                    return "\uE93C";
                }
                if (pageType == "artist")
                {
                    return "\uE77B";
                }
                if (pageType == "folder")
                {
                    return "\uE8B7";
                }
            }
            return "\uE93C";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
