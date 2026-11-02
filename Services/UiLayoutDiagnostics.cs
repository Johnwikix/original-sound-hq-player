using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WinUIMusicPlayer.Services;

/// <summary>有界布局飞行记录；正常布局只写值快照，异常时才格式化控件路径和可视树。</summary>
internal static class UiLayoutDiagnostics
{
    private const int Capacity = 256;
    private const int MaxNodes = 512;
    private const int MaxCharacters = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<FrameworkElement, ElementInfo> Elements = new();
    private static readonly Entry[] History = new Entry[Capacity];
    private static int _next;
    private static int _count;
    private static int _reports;
    private static volatile bool _stopping;
    private static Task _write = Task.CompletedTask;
    private static long _lastReport;
    private static string _directory = "";
    private static Func<DependencyObject?>? _root;
    private static Action<string>? _log;
    private static string _context = "Starting";

    private sealed class ElementInfo(FrameworkElement element)
    {
        internal readonly WeakReference<FrameworkElement> Target = new(element);
        internal readonly string Type = element.GetType().FullName ?? element.GetType().Name;
        internal string Name = element.Name;
    }
    private readonly record struct Entry(long Time, ElementInfo? Element, string Kind, double X, double Y, double OldX, double OldY);

    /// <summary>在 UI 线程初始化；WinUI 原生详细追踪仅在布局循环即将失败时输出到调试器和转储。</summary>
    internal static void Initialize(string directory, Func<DependencyObject?> root, Action<string> log)
    {
        _directory = directory;
        _root = root;
        _log = log;
        try { Application.Current.DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High; }
        catch (Exception error) { SafeLog("Native layout tracing unavailable: " + error.GetType().Name); }
    }

    /// <summary>记录页面导航，不记录歌曲名称、URL、凭据或控件文本。</summary>
    internal static void SetContext(string context) => _context = context;

    /// <summary>跟踪视图根；弱表和事件均不把页面保留在全局强引用中。</summary>
    internal static void Track(FrameworkElement element)
    {
        if (_root is null || _stopping || Elements.TryGetValue(element, out _)) return;
        Elements.Add(element, new ElementInfo(element));
        element.SizeChanged += SizeChanged;
        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (Elements.TryGetValue(element, out var info)) info.Name = element.Name;
        Record(element, "Loaded", element.ActualWidth, element.ActualHeight);
        if (element is Page or UserControl)
        {
            try
            {
                int visited = 0;
                TrackTree(element, 0, ref visited);
            }
            catch (Exception error) { SafeLog("UI diagnostic tracking incomplete: " + error.GetType().Name); }
        }
    }
    private static void Unloaded(object sender, RoutedEventArgs args) => Record((FrameworkElement)sender, "Unloaded", 0, 0);
    private static void SizeChanged(object sender, SizeChangedEventArgs args) => Record((FrameworkElement)sender,
        "SizeChanged", args.NewSize.Width, args.NewSize.Height, args.PreviousSize.Width, args.PreviousSize.Height);
    /// <summary>保存自定义控件的测量约束，覆盖循环期间未产生 SizeChanged 的情况。</summary>
    internal static void Measure(FrameworkElement element, Size size) => Record(element, "Measure", size.Width, size.Height);
    private static void Record(FrameworkElement element, string kind, double x, double y, double oldX = 0, double oldY = 0)
    {
        if (_root is null || _stopping || !Elements.TryGetValue(element, out var info)) return;
        lock (Gate)
        {
            History[_next] = new(Stopwatch.GetTimestamp(), info, kind, x, y, oldX, oldY);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
        }
    }
    private static void TrackTree(DependencyObject node, int depth, ref int visited)
    {
        if (depth > 24 || visited++ >= MaxNodes) return;
        if (node is FrameworkElement element && (element.Name.Length > 0 || element is UserControl or Page or Frame or ScrollViewer)) Track(element);
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count && visited < MaxNodes; i++) TrackTree(VisualTreeHelper.GetChild(node, i), depth + 1, ref visited);
    }

    /// <summary>在 UI 异常边界同步捕获值，后台写盘；最多每 30 秒一次、每进程六次。</summary>
    internal static void Capture(Exception exception)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (_root is null || _stopping || _reports >= 6 || (_reports > 0 && Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds < 30)) return;
            _lastReport = now;
            _reports++;
            Entry[] entries;
            lock (Gate)
            {
                entries = new Entry[_count];
                for (int i = 0; i < _count; i++) entries[i] = History[(_next - _count + i + Capacity) % Capacity];
            }
            var text = new StringBuilder(32768);
            text.AppendLine($"Time={DateTimeOffset.Now:O} Process={Environment.ProcessId} Context={_context}");
            text.AppendLine(exception.ToString());
            text.AppendLine("Recent layout events (old -> new / Measure constraint; candidates, not proof of root cause):");
            foreach (var entry in entries)
            {
                var info = entry.Element;
                text.Append($"{-Stopwatch.GetElapsedTime(entry.Time, now).TotalMilliseconds:F2}ms {entry.Kind} {info?.Type}#{info?.Name} {entry.OldX:F3}x{entry.OldY:F3} -> {entry.X:F3}x{entry.Y:F3}");
                try
                {
                    if (info?.Target.TryGetTarget(out var element) == true) text.Append(" Path=" + GetPath(element));
                }
                catch (Exception error) { text.Append(" [path unavailable: " + error.GetType().Name + "]"); }
                text.AppendLine();
            }
            text.AppendLine("Visible tree: Type#x:Name actual / desired / Width,Height / Min,Max / Margin");
            try
            {
                if (_root() is { } root)
                {
                    int visited = 0;
                    DumpTree(root, text, 0, ref visited);
                    if (visited >= MaxNodes) text.AppendLine("[tree truncated: node limit]");
                }
            }
            catch (Exception error) { text.AppendLine("[tree unavailable: " + error.GetType().Name + "]"); }
            string detail = text.ToString(0, Math.Min(text.Length, MaxCharacters));
            string path = Path.Combine(_directory, $"Layout-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            SafeLog("UI layout diagnostic captured: " + path);
            lock (Gate)
            {
                if (!_stopping) _write = WriteAfterAsync(_write, path, detail);
            }
        }
        catch (Exception error) { SafeLog("UI layout diagnostic capture failed: " + error.GetType().Name); }
    }
    private static string GetPath(DependencyObject node)
    {
        var path = new StringBuilder(256);
        for (int depth = 0; node is not null && depth < 24; depth++, node = VisualTreeHelper.GetParent(node))
        {
            string name = node is FrameworkElement element ? element.Name : "";
            if (depth > 0) path.Insert(0, '/');
            path.Insert(0, node.GetType().Name + (name.Length > 0 ? "#" + name : ""));
        }
        return path.ToString();
    }
    private static void DumpTree(DependencyObject node, StringBuilder text, int depth, ref int visited)
    {
        if (depth > 24 || visited++ >= MaxNodes || text.Length >= MaxCharacters) return;
        if (node is FrameworkElement element)
        {
            if (element.Visibility == Visibility.Collapsed) return;
            text.Append(' ', depth * 2).AppendLine($"{element.GetType().Name}#{element.Name} actual={element.ActualWidth:F3}x{element.ActualHeight:F3} desired={element.DesiredSize} size={element.Width},{element.Height} min={element.MinWidth},{element.MinHeight} max={element.MaxWidth},{element.MaxHeight} margin={element.Margin}");
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count && visited < MaxNodes; i++) DumpTree(VisualTreeHelper.GetChild(node, i), text, depth + 1, ref visited);
    }
    /// <summary>退出时拒绝迟到记录，并等待已经捕获的日志完成落盘。</summary>
    internal static Task StopAsync()
    {
        lock (Gate)
        {
            _stopping = true;
            return _write;
        }
    }
    private static async Task WriteAfterAsync(Task previous, string path, string detail)
    {
        await previous.ConfigureAwait(false);
        await WriteAsync(path, detail).ConfigureAwait(false);
    }
    private static async Task WriteAsync(string path, string detail)
    {
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, detail.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
                string[] files = Directory.GetFiles(Path.GetDirectoryName(path)!, "Layout-*.log");
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length - 20; i++)
                {
                    try { File.Delete(files[i]); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }).ConfigureAwait(false);
            SafeLog("UI layout diagnostic saved: " + path);
        }
        catch (Exception error) { SafeLog("UI layout diagnostic write failed: " + error.GetType().Name); }
    }
    private static void SafeLog(string message)
    {
        try { _log?.Invoke(message); } catch { }
    }
}
