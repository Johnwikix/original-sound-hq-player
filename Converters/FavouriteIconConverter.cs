using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public partial class FavouriteIconConverter : IValueConverter
    {
        private const string FAVOURITE_ICON = "\ueb52"; // 填充的星星
        private const string NOT_FAVOURITE_ICON = "\ueb51"; // 空心的星星

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isFavourite)
            {
                return isFavourite ? FAVOURITE_ICON : NOT_FAVOURITE_ICON;
            }
            return NOT_FAVOURITE_ICON;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string icon)
            {
                // 比较图标字符串是否匹配收藏状态的图标
                return string.Equals(icon, FAVOURITE_ICON, StringComparison.Ordinal);
            }
            // 非字符串值默认返回false
            return false;
        }
    }
}
