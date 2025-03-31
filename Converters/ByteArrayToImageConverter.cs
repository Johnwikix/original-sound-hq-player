using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace WinUIMusicPlayer.Converters
{
    public class ByteArrayToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is byte[] byteArray && byteArray.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(byteArray))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                        return bitmapImage;
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
