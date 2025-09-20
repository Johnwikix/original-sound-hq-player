using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumSongsConverter : IValueConverter
    {    
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string album && !string.IsNullOrWhiteSpace(album))
            {                
                return AppData.allSongs.AsValueEnumerable().Where(m=>m.Album == album).Count();
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
