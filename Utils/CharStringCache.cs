using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Utils
{
    public static class CharStringCache
    {
        private static readonly string HashTag = "#";
        // 缓存所有可能的结果（#, 中, A-Z）
        // 数组索引 0-25 存储 A-Z
        // 索引 26 存储 #
        // 索引 27 存储 中
        private static readonly string[] Cache = new string[28];

        static CharStringCache()
        {
            // 预先创建 A-Z
            for (int i = 0; i < 26; i++)
            {
                Cache[i] = ((char)('A' + i)).ToString();
            }
            // 预先创建特殊字符
            Cache[26] = "#";
            Cache[27] = "中";
        }

        // 快速查找 A-Z 的缓存结果
        public static string GetLetter(char c)
        {
            int index = c - 'A';
            if (index >= 0 && index < 26)
            {
                return Cache[index];
            }
            // 理论上调用者应该保证只传入 A-Z
            return HashTag;
        }

        // 快速查找特殊字符的缓存结果
        public static string GetHashTag() => Cache[26];
        public static string GetZhongChar() => Cache[27];
    }
}
