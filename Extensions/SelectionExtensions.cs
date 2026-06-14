using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Extensions
{
    public static class SelectionExtensions
    {
        public static void AddRange<T>(this IList<object> target, ReadOnlySpan<T> source) where T : class
        {
            for (int i = 0; i < source.Length; i++)
                target.Add(source[i]);
        }

        public static void AddRangeIfNotNull<T>(this IList<object> target, ReadOnlySpan<T?> source) where T : class
        {
            for (int i = 0; i < source.Length; i++)
            {
                var item = source[i];
                if (item is not null) target.Add(item);
            }
        }

        public static void ReplaceWith(this IList<object> target, ReadOnlySpan<object> source)
        {
            target.Clear();
            for (int i = 0; i < source.Length; i++)
                target.Add(source[i]);
        }
    }
}
