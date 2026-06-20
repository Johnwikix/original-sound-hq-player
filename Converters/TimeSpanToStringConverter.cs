using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace WinUIMusicPlayer.Converters
{
    public partial class TimeSpanToStringConverter : IValueConverter
    {
        private readonly ConcurrentDictionary<long, string> _cache = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not TimeSpan timeSpan) return string.Empty;

            long key = timeSpan.Ticks;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            string result = timeSpan.TotalHours >= 1
                ? string.Create(8, timeSpan, static (span, ts) => WriteWithHours(span, ts))
                : string.Create(5, timeSpan, static (span, ts) => WriteNoHours(span, ts));

            _cache[key] = result;
            return result;
        }

        private static void WriteWithHours(Span<char> dst, TimeSpan ts)
        {
            ts.Hours.TryFormat(dst.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
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
