using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class SampleRateBitDepthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Tuple<int, int> values)
            {
                int sampleRate = values.Item1;
                int bitDepth = values.Item2;
                if (sampleRate >= 48000 && bitDepth >= 24)
                {
                    return Visibility.Visible;
                }
                else if (bitDepth == 1 && sampleRate >= 2822400)
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
