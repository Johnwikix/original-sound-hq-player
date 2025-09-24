using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters
{
    public class RadioButtonTagToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // value是ViewModel中的属性值（例如"Mica"）
            // parameter是RadioButton的Tag值
            if (value is null || parameter is null)
                return false;

            return value.ToString().Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            // value是RadioButton的IsChecked值
            // parameter是RadioButton的Tag值
            if (value is bool isChecked && isChecked && parameter is not null)
            {
                return parameter.ToString();
            }

            // 返回DependencyProperty.UnsetValue表示不更新源属性
            return Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }
}
