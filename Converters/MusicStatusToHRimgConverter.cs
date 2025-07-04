using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Converters
{
    public class MusicStatusToHRimgConverter : IValueConverter
    {
        object IValueConverter.Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Music music) {
                if ((music.SampleRate >= 48000 && music.BitDepth >= 24) || (music.SampleRate >= 2822400 && music.BitDepth == 1))
                {
                    var bitmapImage = new BitmapImage(new Uri("ms-appx:///Assets/hr.png"));
                    return bitmapImage;
                }                
            }
            return null;
        }

        object IValueConverter.ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
