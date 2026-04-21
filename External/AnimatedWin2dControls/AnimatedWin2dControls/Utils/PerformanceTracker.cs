// PerformanceTracker.cs — 放在 Utils 文件夹
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AnimatedWin2dControls.Utils
{
    internal static class PerformanceTracker
    {
        // 每个 probe 的统计数据
        private sealed class Stat
        {
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public double LastMs;
        }

        private static readonly ConcurrentDictionary<string, Stat> _stats = new();

        // ── 计时 scope（using 语法糖）────────────────────────────────────────
        public readonly struct Scope : IDisposable
        {
            private readonly string _key;
            private readonly long _start;
            public Scope(string key) { _key = key; _start = Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _start)
                            * 1000.0 / Stopwatch.Frequency;
                Record(_key, ms);
            }
        }

        public static Scope Measure(string key) => new(key);

        public static void Record(string key, double ms)
        {
            var s = _stats.GetOrAdd(key, _ => new Stat());
            // 非原子，但对于诊断工具可接受
            s.Count++;
            s.TotalMs += ms;
            if (ms > s.MaxMs) s.MaxMs = ms;
            s.LastMs = ms;

            // 超阈值立即打警告
            if (ms > 8.0)
                Debug.WriteLine($"[Perf ⚠] {key} took {ms:F1} ms  (count={s.Count})");
        }

        // ── 每隔 N 帧打一次汇总 ───────────────────────────────────────────────
        private static int _frameCnt;
        private const int ReportInterval = 120; // 约每 2 秒

        public static void FrameTick()
        {
            if (++_frameCnt < ReportInterval) return;
            _frameCnt = 0;
            PrintReport();
        }

        public static void PrintReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("── AlbumArtControl perf report ──────────────────");
            foreach (var (key, s) in _stats)
            {
                double avg = s.Count > 0 ? s.TotalMs / s.Count : 0;
                sb.AppendLine(
                    $"  {key,-28}  avg={avg,5:F1}ms  max={s.MaxMs,5:F1}ms  last={s.LastMs,5:F1}ms  n={s.Count}");
            }
            sb.AppendLine("─────────────────────────────────────────────────");
            Debug.WriteLine(sb.ToString());
        }

        public static void Reset() => _stats.Clear();
    }
}