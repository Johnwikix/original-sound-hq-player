#if DEBUG
using Microsoft.UI.Dispatching;
using ObservableCollections;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WinUIMusicPlayer.Helper
{
    public sealed class CollectionBenchmark
    {
        public sealed class Result
        {
            public string Scenario { get; set; } = string.Empty;
            public int Iterations { get; set; }
            public double MedianMs { get; set; }
            public double P95Ms { get; set; }
            public long MedianAllocBytes { get; set; }
            public long P95AllocBytes { get; set; }
        }

        public static List<Result> Run(DispatcherQueue dispatcherQueue)
        {
            var results = new List<Result>();
            const int N = 10_000;
            const int Iterations = 10;
            const int Warmup = 2;

            for (int w = 0; w < Warmup; w++)
            {
                _ = RunScenarioS1_Load(N);
                _ = RunScenarioS2_Sort(N);
                _ = RunScenarioS3_AlbumFilter(N);
                _ = RunScenarioS4_Search(N);
                _ = RunScenarioS5_Shuffle(N);
            }

            results.Add(Measure("S1_Load_10K", () => RunScenarioS1_Load(N), Iterations));
            results.Add(Measure("S2_Sort_Change", () => RunScenarioS2_Sort(N), Iterations));
            results.Add(Measure("S3_AlbumFilter_Change", () => RunScenarioS3_AlbumFilter(N), Iterations));
            results.Add(Measure("S4_Search_Change", () => RunScenarioS4_Search(N), Iterations));
            results.Add(Measure("S5_Shuffle", () => RunScenarioS5_Shuffle(N), Iterations));

            return results;
        }

        public static void PrintReport(IReadOnlyList<Result> results, Action<string> log)
        {
            log("===== Collection Benchmark =====");
            foreach (var r in results)
            {
                log($"{r.Scenario,-30} median={r.MedianMs,7:F2}ms p95={r.P95Ms,7:F2}ms  alloc median={r.MedianAllocBytes,8} p95={r.P95AllocBytes,8}");
            }
            log("================================");
        }

        private static Result Measure(string name, Func<long> scenario, int iterations)
        {
            var times = new double[iterations];
            var allocs = new long[iterations];
            for (int i = 0; i < iterations; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long startBytes = GC.GetTotalAllocatedBytes(precise: true);
                var sw = Stopwatch.StartNew();
                long allocDelta = scenario();
                sw.Stop();
                long endBytes = GC.GetTotalAllocatedBytes(precise: true);
                times[i] = sw.Elapsed.TotalMilliseconds;
                allocs[i] = (endBytes - startBytes) + allocDelta;
            }
            Array.Sort(times);
            Array.Sort(allocs);
            return new Result
            {
                Scenario = name,
                Iterations = iterations,
                MedianMs = times[times.Length / 2],
                P95Ms = times[Math.Min(times.Length - 1, (int)(times.Length * 0.95))],
                MedianAllocBytes = allocs[allocs.Length / 2],
                P95AllocBytes = allocs[Math.Min(allocs.Length - 1, (int)(allocs.Length * 0.95))],
            };
        }

        private static long RunScenarioS1_Load(int n)
        {
            var src = new ObservableList<int>(n);
            var view = src.CreateView(static x => x);
            for (int i = 0; i < n; i++) src.Add(i);
            return 0;
        }

        private static long RunScenarioS2_Sort(int n)
        {
            var src = new ObservableList<int>(n);
            for (int i = 0; i < n; i++) src.Add(n - i);
            long extra = 0;
            for (int i = 0; i < 5; i++)
            {
                src.Sort(i % 2 == 0 ? Comparer<int>.Default : Comparer<int>.Create((a, b) => b.CompareTo(a)));
                extra++;
            }
            return extra;
        }

        private static long RunScenarioS3_AlbumFilter(int n)
        {
            var src = new ObservableList<int>(n);
            for (int i = 0; i < n; i++) src.Add(i);
            var view = src.CreateView(static x => x);
            long extra = 0;
            for (int target = 0; target < 5; target++)
            {
                int t = target;
                view.AttachFilter(new LambdaFilter<int, int>(x => x == t));
                extra++;
            }
            return extra;
        }

        private static long RunScenarioS4_Search(int n)
        {
            var src = new ObservableList<int>(n);
            for (int i = 0; i < n; i++) src.Add(i);
            var view = src.CreateView(static x => x);
            long extra = 0;
            for (int k = 0; k < 5; k++)
            {
                int kk = k;
                view.AttachFilter(new LambdaFilter<int, int>(x => (x % (kk + 1)) == 0));
                extra++;
            }
            return extra;
        }

        private static long RunScenarioS5_Shuffle(int n)
        {
            var list = new ObservableList<int>(n);
            for (int i = 0; i < n; i++) list.Add(i);
            var rng = new Random(42);
            int count = list.Count;
            while (count > 1)
            {
                count--;
                int k = rng.Next(count + 1);
                if (k != count) list.Move(count, k);
            }
            return 0;
        }

        private sealed class LambdaFilter<T, TView> : ISynchronizedViewFilter<T, TView>
        {
            private readonly Func<T, bool> _predicate;
            public LambdaFilter(Func<T, bool> predicate) { _predicate = predicate; }
            public bool IsMatch(T value, TView view) => _predicate(value);
        }
    }
}
#endif
