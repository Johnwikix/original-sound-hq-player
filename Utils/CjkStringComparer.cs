using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinUIMusicPlayer.Utils
{
    /// <summary>
    /// CJK 感知的字符串比较器，按首个非空白字符分桶，桶间顺序与 Ordinal 下的相对顺序一致：
    /// 拉丁/其他 &lt; 假名（日语，ja 文化的五十音序）&lt; 汉字（zh-CN 文化的拼音序）。
    /// 纯汉字的日语词无法从字形判断读音，仍落入汉字桶按拼音序参与排序。
    /// </summary>
    public static class CjkStringComparer
    {
        private const char LatinTierEnd = '\u2E80';

        private static readonly StringComparer s_zhCN =
            StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.IgnoreCase);
        private static readonly StringComparer s_ja =
            StringComparer.Create(CultureInfo.GetCultureInfo("ja"), CompareOptions.IgnoreCase);

        /// <summary>供 OrderBy/Sort 等 API 使用的比较器实例。</summary>
        public static readonly IComparer<string> Instance = Comparer<string>.Create(Compare);

        public static int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int xTier = GetTier(x);
            int yTier = GetTier(y);
            if (xTier != yTier) return xTier.CompareTo(yTier);

            return (xTier == TierKana ? s_ja : s_zhCN).Compare(x, y);
        }

        private const int TierLatin = 0;
        private const int TierKana = 1;
        private const int TierHan = 2;

        private static int GetTier(string s)
        {
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c < LatinTierEnd) return TierLatin;
                if (IsKana(c)) return TierKana;
                return TierHan;
            }
            return TierLatin;
        }

        private static bool IsKana(char c) =>
            (c >= '\u3040' && c <= '\u30FF')   // 平假名 + 片假名
            || (c >= '\u31F0' && c <= '\u31FF') // 片假名音标扩展
            || (c >= '\uFF66' && c <= '\uFF9F'); // 半角片假名
    }
}
