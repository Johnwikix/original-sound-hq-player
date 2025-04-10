using Microsoft.UI.Xaml.Data;

namespace WinUIMusicPlayer.Converters
{
    public class FavouriteTextConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, string language)
        {
            if (value is bool isFavorite)
            {
                return isFavorite ? "取消最爱" : "设为最爱";
            }
            return "设为最爱";
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        {
            throw new System.NotImplementedException();
        }
    }
}