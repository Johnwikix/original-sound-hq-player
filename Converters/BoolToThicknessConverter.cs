//using Microsoft.UI.Xaml;
//using Microsoft.UI.Xaml.Data;
//using System;

//namespace WinUIMusicPlayer.Converters
//{
//    public class BoolToThicknessConverter : IValueConverter
//    {
//        public object Convert(object value, Type targetType, object parameter, string language)
//        {
//            if (value is bool playingDetail)
//            {
//                return playingDetail ? new Thickness(0, 0, 0, 0) : new Thickness(0, 32, 0, 0);
//            }
//            return new Thickness(0, 32, 0, 0); // Default value if not a boolean
//        }

//        public object ConvertBack(object value, Type targetType, object parameter, string language)
//        {
//            throw new NotImplementedException();
//        }
//    }
//}
