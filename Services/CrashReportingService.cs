using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>
/// 崩溃与异常诊断：把错误信息送入 Windows 错误报告（WER）管道。
/// 两条路径：
/// 1. 已处理异常（进程不退出）：ReportHandledException 用 WER 报告 API 主动生成一份
///    NonCritical 报告（静默入队、不弹 UI，用户同意后后台上传），附带异常详情文件、
///    当前日志与一份当前进程转储——托管异常类型/消息/栈顶作为报告参数，这弥补了
///    .NET 崩溃在 WER 里只有原生偏移、无托管堆栈的问题；
/// 2. 致命异常（进程即将终止，如后台线程未处理异常）：OnFatalException 把当前日志注册进
///    WER 即将生成的真实崩溃报告并刷盘；createdump 环境变量在本地落 Mini 转储，
///    配合提交商店的符号包（.appxsym）可还原托管堆栈。
/// 说明：清单级声明 WER 运行时异常辅助模块（desktop7:ErrorReporting / 未来的
/// windows.diagnosticServiceModule）因 classicAppCompat 自定义能力墙而不可用，详见
/// Package.appxmanifest 注释与 docs/wer-diagnostics-2026-09-18.md。
/// 本服务会被崩溃路径调用，因此保持静态、无依赖、所有操作均为尽力而为不抛出。
/// </summary>
internal static class CrashReportingService
{
    private const int WerRegFileTypeOther = 2;
    private const int MaxDumpAgeDays = 14;
    private const int WerReportNonCritical = 0;
    private const int WerFileTypeOther = 5;
    private const int WerDumpTypeMiniDump = 2;
    private const int WerConsentNotAsked = 1;
    private const int WerP0 = 0;
    private const int WerP1 = 1;
    private const int WerP2 = 2;
    // WER_SUBMIT_QUEUE | WER_SUBMIT_OUTOFPROCESS | WER_SUBMIT_ADD_REGISTERED_DATA
    private const int WerSubmitFlags = 4 | 32 | 16;

    // WER 参数值上限为 MAX_PATH（werapi.h: WER_MAX_PARAM_LENGTH）。
    private const int MaxParameterValueLength = 260;

    // 会话内去重与限额：同一签名只报一次、总量封顶，防止故障风暴灌爆 WER 队列
    //（同一 UI 回调反复抛异常的场景）。
    private const int MaxReportsPerSession = 10;
    private static readonly object ReportGate = new();
    private static readonly HashSet<string> ReportedSignatures = new(StringComparer.Ordinal);
    private static int _reportCount;

    private const string EventTypeName = "OriginalSoundHandledException";

    private static string _logDirectory = string.Empty;

    /// <summary>在 App 构造函数最早处调用：配置本地崩溃转储兜底并清理过期转储。</summary>
    public static void Initialize(string logDirectory)
    {
        _logDirectory = logDirectory;
        try
        {
            // createdump 在崩溃瞬间由运行时拉起并读取这些变量；用户环境已显式设置的值优先，不做覆盖。
            var dumpDirectory = Path.Combine(
                Path.GetDirectoryName(logDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? logDirectory,
                "CrashDumps");
            Directory.CreateDirectory(dumpDirectory);
            SetIfAbsent("DOTNET_DbgEnableMiniDump", "1");
            // 1 = Mini：仅模块/线程列表、异常信息与全部堆栈，体积小且不含堆内存，避免在用户磁盘上留存大量隐私数据。
            SetIfAbsent("DOTNET_DbgMiniDumpType", "1");
            // %p=进程 ID，%t= epoch 秒，保证多次启动的转储互不覆盖。
            SetIfAbsent("DOTNET_DbgMiniDumpName", Path.Combine(dumpDirectory, "crash_%p_%t.dmp"));
            CleanupOldDumps(dumpDirectory);
        }
        catch
        {
            // 兜底配置失败不阻止应用启动。
        }
    }

    /// <summary>
    /// 已处理异常上报：进程不退出。同步捕获字符串证据后转后台线程执行 WER 报告
    /// （生成转储与提交耗时百毫秒级，不能卡 UI 线程）。
    /// </summary>
    public static void ReportHandledException(Exception exception)
    {
        try
        {
            if (!TryReserveReport(exception)) return;

            string type = exception.GetType().FullName ?? string.Empty;
            string message = exception.Message ?? string.Empty;
            string topFrame = (exception.StackTrace ?? string.Empty)
                .Split('\n')
                .FirstOrDefault(line => line.TrimStart().StartsWith("at "))?
                .Trim() ?? string.Empty;
            string detail = exception.ToString();
            string? logFile = FindCurrentLogFile();

            _ = Task.Run(() => SubmitReport(type, message, topFrame, detail, logFile));
        }
        catch
        {
            // 上报失败不影响应用继续运行。
        }
    }

    /// <summary>
    /// 致命异常路径（进程即将终止）调用：把当前日志附加到 WER 报告并同步刷盘日志。
    /// 此时进程环境仍可写，崩溃报告生成时会把已注册文件一并收集。
    /// </summary>
    public static void OnFatalException()
    {
        try
        {
            var currentLog = FindCurrentLogFile();
            if (currentLog is not null)
            {
                // 不声明 WER_FILE_ANONYMOUS_DATA：日志含文件路径等用户数据，不能按匿名数据上报。
                _ = WerRegisterFile(currentLog, WerRegFileTypeOther, 0);
            }
        }
        catch
        {
            // 崩溃路径必须零抛出。
        }

        try
        {
            Serilog.Log.CloseAndFlush();
        }
        catch
        {
            // 刷盘失败时至少 WER 转储已覆盖主要信息。
        }
    }

    private static bool TryReserveReport(Exception exception)
    {
        lock (ReportGate)
        {
            if (_reportCount >= MaxReportsPerSession) return false;
            var signature = exception.GetType().FullName + "\n" + exception.StackTrace;
            if (!ReportedSignatures.Add(signature)) return false;
            _reportCount++;
            return true;
        }
    }

    private static void SubmitReport(string type, string message, string topFrame, string detail, string? logFile)
    {
        string? detailFile = null;
        IntPtr report = IntPtr.Zero;
        try
        {
            detailFile = Path.Combine(Path.GetTempPath(), $"oshp-exception-{Environment.TickCount64:x}.txt");
            File.WriteAllText(detailFile, detail);

            var info = new WerReportInformation
            {
                dwSize = Marshal.SizeOf<WerReportInformation>(),
                // MSIX 安装路径（WindowsApps 下）可能超过字段上限，超长会使整个封送失败，
                // 因此所有内联字符串字段都先截断。
                wzFriendlyEventName = TruncateTo("OriginalSound HIFI Player handled exception", 127),
                wzApplicationName = TruncateTo("OriginalSound HIFI Player", 127),
                wzApplicationPath = TruncateTo(Environment.ProcessPath ?? string.Empty, 259),
                wzDescription = TruncateTo("Handled .NET exception captured while the application kept running.", 511),
            };
            if (WerReportCreate(EventTypeName, WerReportNonCritical, ref info, out report) != 0 || report == IntPtr.Zero)
            {
                return;
            }

            // WER 报告的桶参数：异常类型/消息/栈顶直接可读，完整堆栈见附带详情文件。
            _ = WerReportSetParameter(report, WerP0, "ExceptionType", Truncate(type));
            _ = WerReportSetParameter(report, WerP1, "ExceptionMessage", Truncate(message));
            _ = WerReportSetParameter(report, WerP2, "TopFrame", Truncate(topFrame));
            _ = WerReportAddFile(report, detailFile, WerFileTypeOther, 0);
            if (logFile is not null)
            {
                _ = WerReportAddFile(report, logFile, WerFileTypeOther, 0);
            }
            _ = WerReportAddDump(report, GetCurrentProcess(), IntPtr.Zero, WerDumpTypeMiniDump,
                IntPtr.Zero, IntPtr.Zero, 0);
            _ = WerReportSubmit(report, WerConsentNotAsked, WerSubmitFlags, out _);
        }
        catch
        {
            // 尽力而为：WER 失败不影响应用。
        }
        finally
        {
            if (report != IntPtr.Zero) _ = WerReportCloseHandle(report);
            if (detailFile is not null)
            {
                try { File.Delete(detailFile); } catch (IOException) { }
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxParameterValueLength ? value : value[..MaxParameterValueLength];

    private static string TruncateTo(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void SetIfAbsent(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is null)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string? FindCurrentLogFile()
    {
        if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory)) return null;
        // Serilog 按日滚动（WinUIMusicPlayer-YYYYMMDD.log），取最近写入的一个即为当前文件。
        return Directory.EnumerateFiles(_logDirectory, "*.log")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    private static void CleanupOldDumps(string dumpDirectory)
    {
        if (!Directory.Exists(dumpDirectory)) return;
        var cutoff = DateTime.UtcNow.AddDays(-MaxDumpAgeDays);
        foreach (var file in Directory.EnumerateFiles(dumpDirectory, "crash_*.dmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (IOException)
            {
                // 单个文件被占用等失败跳过，不影响其余清理。
            }
        }
    }

    [DllImport("werapi.dll", ExactSpelling = true)]
    private static extern int WerRegisterFile(string pwzFile, int regFileType, int dwFlags);

    [DllImport("werapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WerReportCreate(string pwzEventType, int repType,
        ref WerReportInformation pReportInformation, out IntPtr phReportHandle);

    [DllImport("werapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WerReportSetParameter(IntPtr hReportHandle, int dwparamID,
        string? pwzName, string pwzValue);

    [DllImport("werapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WerReportAddFile(IntPtr hReportHandle, string pwzPath, int repFileType, int dwFileFlags);

    [DllImport("werapi.dll", ExactSpelling = true)]
    private static extern int WerReportAddDump(IntPtr hReportHandle, IntPtr hProcess, IntPtr hThread,
        int dumpType, IntPtr pExceptionParam, IntPtr pDumpCustomOptions, int dwFlags);

    [DllImport("werapi.dll", ExactSpelling = true)]
    private static extern int WerReportSubmit(IntPtr hReportHandle, int consent, int dwFlags, out int pSubmitResult);

    [DllImport("werapi.dll", ExactSpelling = true)]
    private static extern int WerReportCloseHandle(IntPtr hReportHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>WER_REPORT_INFORMATION 的封送布局；字段长度与 werapi.h 中固定数组一致。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WerReportInformation
    {
        public int dwSize;
        public IntPtr hProcess;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string wzConsentKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string wzFriendlyEventName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string wzApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wzApplicationPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string wzDescription;
        public IntPtr hwndParent;
    }
}
