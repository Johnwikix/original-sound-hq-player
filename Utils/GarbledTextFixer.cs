using System;
using System.Text;

namespace WinUIMusicPlayer.Utils
{
    public static class GarbledTextFixer
    {
        static GarbledTextFixer()
        {
            // 注册编码提供程序以支持GBK等编码
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        public static string FixGbkToIso88591(string corruptedText)
        {
            try
            {
                // 1. 将乱码文本按照 ISO-8859-1 编码转换为字节数组
                // 这样可以恢复原始的 GBK 字节
                Encoding iso88591 = Encoding.GetEncoding("ISO-8859-1");
                byte[] originalGbkBytes = iso88591.GetBytes(corruptedText);

                // 2. 将这些字节按照 GBK 编码解释为正确的字符串
                Encoding gbkEncoding = Encoding.GetEncoding("GBK");
                string fixedText = gbkEncoding.GetString(originalGbkBytes);
                return fixedText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"修复编码时出错: {ex.Message}");
                return corruptedText;
            }
        }

        public static string FixIso88591ToGbk(string corruptedText)
        {
            try
            {
                // 1. 将乱码文本按照 ISO-8859-1 编码转换为字节数组
                // 这样可以恢复原始的 GBK 字节
                Encoding gbkEncoding = Encoding.GetEncoding("GBK");
                byte[] originalGbkBytes = gbkEncoding.GetBytes(corruptedText);

                // 2. 将这些字节按照 GBK 编码解释为正确的字符串
                Encoding iso88591 = Encoding.GetEncoding("ISO-8859-1");
                string fixedText = iso88591.GetString(originalGbkBytes);
                return fixedText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"修复编码时出错: {ex.Message}");
                return corruptedText;
            }
        }

        public static bool IsGbkToIso88591Garbled(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            // 1. 检查是否包含典型的乱码字符模式
            if (ContainsTypicalGarbledChars(text))
            {
                // 2. 尝试修复后检查是否变成合理的中文
                try
                {
                    string fixed_ = FixGbkToIso88591(text);
                    return IsReasonableChinese(fixed_);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool ContainsTypicalGarbledChars(string text)
        {
            // GBK字节被ISO-8859-1解释后常见的字符范围
            int suspiciousCharCount = 0;
            int totalChars = text.Length;

            foreach (char c in text)
            {
                // ISO-8859-1 中 128-255 范围的字符在GBK乱码中很常见
                if (c >= 128 && c <= 255)
                {
                    suspiciousCharCount++;
                }
                // 一些特定的常见乱码字符
                else if ("ÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞßàáâãäåæçèéêëìíîïðñòóôõö÷øùúûüýþÿ".Contains(c))
                {
                    suspiciousCharCount++;
                }
            }

            // 如果超过50%的字符是可疑字符，可能是乱码
            return (double)suspiciousCharCount / totalChars > 0.25;
        }

        private static bool IsReasonableChinese(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            int chineseCharCount = 0;
            int totalChars = text.Length;

            foreach (char c in text)
            {
                // 基本汉字范围
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    chineseCharCount++;
                }
                // 扩展汉字范围
                else if (c >= 0x3400 && c <= 0x4DBF)
                {
                    chineseCharCount++;
                }
                // 常用标点符号和英文字母数字也算合理
                else if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c))
                {
                    // 这些字符是可以接受的
                }
            }

            // 如果包含中文字符，且没有明显的乱码特征，认为是合理的
            return chineseCharCount > 0 && chineseCharCount <= totalChars;
        }
    }
}
