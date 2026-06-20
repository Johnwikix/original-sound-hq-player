using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace WinUIMusicPlayer.Converters
{
    public partial class AdvancedThumbToolTipValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not double totalSeconds) return "00:00";
            var timeSpan = TimeSpan.FromSeconds(totalSeconds);
            return timeSpan.TotalHours >= 1
                ? string.Create(8, timeSpan, static (span, ts) => WriteWithHours(span, ts))
                : string.Create(5, timeSpan, static (span, ts) => WriteNoHours(span, ts));
        }

        private static void WriteWithHours(Span<char> dst, TimeSpan ts)
        {
            ((int)ts.TotalHours).TryFormat(dst.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
            dst[2] = ':';
            ts.Minutes.TryFormat(dst.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
            dst[5] = ':';
            ts.Seconds.TryFormat(dst.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
        }

        private static void WriteNoHours(Span<char> dst, TimeSpan ts)
        {
            ts.Minutes.TryFormat(dst.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
            dst[2] = ':';
            ts.Seconds.TryFormat(dst.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
