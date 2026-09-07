using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinUIMusicPlayer.Utils
{
    /// <summary>
    /// 拼音感知的字符串比较器：拉丁开头的字符串排在 CJK 开头的字符串之前（与 Ordinal 下的相对顺序一致），
    /// 同档内部使用 zh-CN 文化排序（ICU/CLDR 的中文排序规则即拼音序）。
    /// </summary>
    public static class PinyinStringComparer
    {
        private static readonly StringComparer s_zhCN =
            StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.IgnoreCase);

        /// <summary>供 OrderBy/Sort 等 API 使用的比较器实例。</summary>
        public static readonly IComparer<string> Instance = Comparer<string>.Create(Compare);

        public static int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            bool xCjk = StartsWithCJK(x);
            bool yCjk = StartsWithCJK(y);
            if (xCjk != yCjk) return xCjk ? 1 : -1;

            return s_zhCN.Compare(x, y);
        }

        private static bool StartsWithCJK(string s)
        {
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                return c >= '\u2E80';
            }
            return false;
        }
    }
}
