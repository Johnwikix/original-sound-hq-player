using Microsoft.UI.Xaml.Data;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class MusicToTitleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Music music)
            {
                if (music is not null)
                {
                    return music.Title;
                }
            }
            return ToolUtils.GetString("AppMainTitle");
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
