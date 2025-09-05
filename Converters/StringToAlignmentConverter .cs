using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Converters
{
    public class StringToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string stringValue)
            {
                // 尝试将字符串解析为 HorizontalAlignment 枚举
                if (Enum.TryParse(stringValue, true, out HorizontalAlignment result))
                {
                    return result;
                }
            }

            // 如果转换失败，返回默认值，比如 Left
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
