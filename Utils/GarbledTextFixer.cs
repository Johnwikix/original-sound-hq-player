using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Utils
{
    public static class GarbledTextFixer
    {
        // 静态构造函数中注册编码提供程序
        static GarbledTextFixer()
        {
            // 注册编码提供程序以支持GBK等编码
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // 常见编码列表，按可能性排序
        private static readonly Encoding[] CommonEncodings = {
        Encoding.UTF8,
        //Encoding.GetEncoding("GB18030"),    // 支持GB2312
        Encoding.Unicode,
        Encoding.BigEndianUnicode,
        Encoding.UTF7,
        Encoding.ASCII,
        //Encoding.GetEncoding("Shift-JIS"), // 增加日文编码
        Encoding.GetEncoding("ISO-8859-1") // 增加西欧编码
    };

        /// <summary>
        /// 尝试修复乱码字符串
        /// </summary>
        /// <param name="garbledText">乱码字符串</param>
        /// <returns>修复后的可能结果</returns>
        public static string[] TryFix(string garbledText)
        {
            if (string.IsNullOrEmpty(garbledText))
                return new string[0];

            var results = new System.Collections.Generic.List<string>();

            foreach (var sourceEncoding in CommonEncodings)
            {
                foreach (var targetEncoding in CommonEncodings)
                {
                    try
                    {
                        byte[] bytes = sourceEncoding.GetBytes(garbledText);
                        string fixedText = targetEncoding.GetString(bytes);

                        if (!string.IsNullOrEmpty(fixedText) && !results.Contains(fixedText))
                        {
                            results.Add(fixedText);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 仅在调试时输出错误信息
                        System.Diagnostics.Debug.WriteLine($"编码转换错误: {ex.Message}");
                    }
                }
            }

            return results.ToArray();
        }

        /// <summary>
        /// 快速修复，针对常见的UTF-8被GBK错误解码的情况
        /// </summary>
        public static string QuickFixUtf8AsGbk(string garbledText)
        {
            try
            {
                byte[] bytes = Encoding.GetEncoding("GB18030").GetBytes(garbledText);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return garbledText;
            }
        }

        /// <summary>
        /// 快速修复，针对常见的GBK被UTF-8错误解码的情况
        /// </summary>
        public static string QuickFixGbkAsUtf8(string garbledText)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(garbledText);
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return garbledText;
            }
        }
    }
}
