using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AnimatedWin2dControls.Utils
{
    internal static class PerformanceTracker
    {
        private sealed class Stat
        {
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public double LastMs;
        }

        private static readonly ConcurrentDictionary<string, Stat> _stats = new();
        private static int _frameCnt;
        private const int ReportInterval = 120;

        public readonly struct Scope : IDisposable
        {
            private readonly string _key;
            private readonly long _start;

            public Scope(string key)
            {
                #if DEBUG
                _key = key;
                _start = Stopwatch.GetTimestamp();
                #endif
            }

            public void Dispose()
            {
                #if DEBUG
                double ms = (Stopwatch.GetTimestamp() - _start)
                            * 1000.0 / Stopwatch.Frequency;
                Record(_key, ms);
                #endif
            }
        }

        public static Scope Measure(string key)
        {
            #if DEBUG
            return new Scope(key);
            #else
            return default;
            #endif
        }

        [Conditional("DEBUG")]
        public static void Record(string key, double ms)
        {
            var s = _stats.GetOrAdd(key, _ => new Stat());
            s.Count++;
            s.TotalMs += ms;
            if (ms > s.MaxMs) s.MaxMs = ms;
            s.LastMs = ms;

            if (ms > 8.0)
                Debug.WriteLine($"[Perf ⚠] {key} took {ms:F1} ms  (count={s.Count})");
        }

        [Conditional("DEBUG")]
        public static void FrameTick()
        {
            if (++_frameCnt < ReportInterval) return;
            _frameCnt = 0;
            PrintReport();
        }

        [Conditional("DEBUG")]
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

        [Conditional("DEBUG")]
        public static void Reset() => _stats.Clear();
    }
}