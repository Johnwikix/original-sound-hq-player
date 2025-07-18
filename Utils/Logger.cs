using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Utils
{
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "OriginalSoundPlayer", "Log");

        private static readonly string LogFilePath = Path.Combine(
            LogDirectory, $"Log_{DateTime.Now:yyyy-MM-dd}.log");

        static Logger()
        {
            try
            {
                // 确保日志目录存在
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                // 写入日志文件头
                if (!File.Exists(LogFilePath))
                {
                    File.AppendAllText(LogFilePath, $"===== Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建日志目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 写入日志
        /// </summary>
        public static void Log(string message, LogLevel level = LogLevel.Information)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{level}] {message}";
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);

                // 同时输出到调试窗口
                Debug.WriteLine(logEntry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"写入日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录异常
        /// </summary>
        public static void LogException(Exception ex, string context = "未指定上下文")
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][错误][{context}]\n" +
                              $"  消息: {ex.Message}\n" +
                              $"  堆栈: {ex.StackTrace}\n" +
                              $"  来源: {ex.Source}\n";

                if (ex.InnerException != null)
                {
                    logEntry += $"  内部异常: {ex.InnerException.Message}\n";
                }

                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);

                // 同时输出到调试窗口
                Debug.WriteLine(logEntry);
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"记录异常失败: {logEx.Message}");
                Debug.WriteLine($"原始异常: {ex.Message}");
            }
        }
    }
}
