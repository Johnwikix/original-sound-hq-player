using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Microsoft.UI.Dispatching;
using WinUIMusicPlayer.Model;

internal static class Program
{
    private static int passed;
    private static void Main()
    {
        DispatcherQueue.Install();
        Test("invalid insertion preserves data and notifications", () =>
        {
            var c = New("a", "b");
            int events = 0;
            c.CollectionChanged += (_, _) => events++;
            Throws<ArgumentOutOfRangeException>(() => c.InsertRange(-1, new[] { "x" }));
            Throws<ArgumentOutOfRangeException>(() => c.InsertRange(3, new[] { "x" }));
            Throws<ArgumentOutOfRangeException>(() => c.InsertRangeWithItemNotifications(3, ["x"]));
            Throws<ArgumentOutOfRangeException>(() => c.InsertRange(3, Array.Empty<string>()));
            Equal(c, "a", "b");
            Check(events == 0);
        });
        Test("invalid removal preserves data", () =>
        {
            var c = New("a", "b");
            Throws<ArgumentOutOfRangeException>(() => c.RemoveRangeWithItemNotifications(-1, 1));
            Throws<ArgumentOutOfRangeException>(() => c.RemoveRangeWithItemNotifications(1, 2));
            Throws<ArgumentOutOfRangeException>(() => c.RemoveRangeWithItemNotifications(0, -1));
            Equal(c, "a", "b");
        });
        Test("self replacement and insertion", () =>
        {
            var c = New("a", "b");
            c.ReplaceAllAsync(c).GetAwaiter().GetResult();
            Equal(c, "a", "b");
            c.InsertRange(1, c);
            Equal(c, "a", "a", "b", "b");
        });
        Test("self addition and single/multiple self removal", () =>
        {
            var c = New("a");
            c.AddRange((IEnumerable<string>)c);
            Equal(c, "a", "a");
            c.RemoveRange(c);
            Check(c.Count == 0);
            c.Add("b");
            c.RemoveRange(c);
            Check(c.Count == 0);
        });
        Test("overlapping FillFrom and append", () =>
        {
            var c = New("a", "b", "c");
            c.FillFrom(c.AsSpan().Slice(1));
            Equal(c, "b", "c");
            c.AddRange(c.AsSpan());
            Equal(c, "b", "c", "b", "c");
            c.FillFrom(c.AsSpan().Slice(0, 1));
            Equal(c, "b");
            c.FillFrom(ReadOnlySpan<string>.Empty);
            Check(c.Count == 0);
        });
        Test("throwing enumerables leave data and events unchanged", () =>
        {
            var c = New("a", "b");
            int events = 0;
            c.CollectionChanged += (_, _) => events++;
            Throws<InvalidOperationException>(() => c.AddRange(Broken()));
            Throws<InvalidOperationException>(() => c.ReplaceAllAsync(Broken()).GetAwaiter().GetResult());
            Throws<InvalidOperationException>(() => c.InsertRange(1, Broken()));
            Throws<InvalidOperationException>(() => c.RemoveRange(Broken()));
            Equal(c, "a", "b");
            Check(events == 0);
            c.Add("after");
            Equal(c, "a", "b", "after"); // guard released after failures
        });
        Test("single pass enumerable is consumed once", () =>
        {
            var input = new Once();
            var c = New("a");
            c.InsertRange(1, input);
            Equal(c, "a", "x", "y");
            Check(input.Enumerations == 1);
        });
        Test("null removal and multiplicities", () =>
        {
            var c = new BulkObservableCollection<string?>(new string?[] { null, "a", null, "a", "b" });
            c.RemoveRange(new string?[] { null, "a" });
            Check(c.SequenceEqual(new string?[] { null, "a", "b" }));
            c.RemoveRange(new string?[] { "a", "b" });
            Check(c.Count == 1 && c[0] is null);
            c.RemoveRange(c);
            Check(c.Count == 0);
        });
        Test("range event indices and stable input during callback", () =>
        {
            var c = New("a", "z");
            var input = new List<string> { "x", "y" };
            var events = new List<string>();
            c.CollectionChanged += (_, e) =>
            {
                events.Add(e.Action + ":" + (e.NewItems?[0] ?? e.OldItems?[0]) + ":" +
                    (e.Action == NotifyCollectionChangedAction.Add ? e.NewStartingIndex : e.OldStartingIndex));
                input.Clear();
                Check(c.Count == (e.Action == NotifyCollectionChangedAction.Add ? 4 : 2));
            };
            c.InsertRangeWithItemNotifications(1, input);
            Equal(c, "a", "x", "y", "z");
            c.RemoveRangeWithItemNotifications(1, 2);
            Equal(c, "a", "z");
            Check(events.SequenceEqual(new[] { "Add:x:1", "Add:y:2", "Remove:x:1", "Remove:y:1" }));
        });
        Test("all mutation entrances reject notification reentrancy", () =>
        {
            var c = New("a", "b");
            c.CollectionChanged += (_, _) =>
            {
                Throws<InvalidOperationException>(() => c.Add("bad"));
                Throws<InvalidOperationException>(() => c.RemoveAt(0));
                Throws<InvalidOperationException>(() => c.Clear());
                Throws<InvalidOperationException>(() => c[0] = "bad");
                Throws<InvalidOperationException>(() => c.Move(0, 1));
                Throws<InvalidOperationException>(() => c.AddRange((IEnumerable<string>)new[] { "bad" }));
                Throws<InvalidOperationException>(() => c.InsertRange(0, new[] { "bad" }));
                Throws<InvalidOperationException>(() => c.RemoveRange(new[] { "a" }));
                Throws<InvalidOperationException>(() => c.FillFrom(new[] { "bad" }));
                Throws<InvalidOperationException>(() => c.SortInPlace(StringComparer.Ordinal));
            };
            c.Add("c");
            Equal(c, "a", "b", "c");
        });
        Test("property notifications also reject reentrancy", () =>
        {
            var c = New("a");
            ((INotifyPropertyChanged)c).PropertyChanged += (_, _) =>
                Throws<InvalidOperationException>(() => c.Clear());
            c.AddRange((IEnumerable<string>)new[] { "b" });
            Equal(c, "a", "b");
        });
        Test("failed comparer preserves data and guard", () =>
        {
            var c = New("c", "b", "a");
            Throws<InvalidOperationException>(() => c.SortInPlace(Comparer<string>.Create((_, _) => throw new Exception("bad comparer"))));
            Equal(c, "c", "b", "a");
            c.SortInPlace(StringComparer.Ordinal);
            Equal(c, "a", "b", "c");
        });
        Test("single Reset per successful whole batch", () =>
        {
            var c = New("a");
            int count = 0;
            c.CollectionChanged += (_, e) => { Check(e.Action == NotifyCollectionChangedAction.Reset); count++; };
            c.AddRange((IEnumerable<string>)new[] { "b", "c" });
            c.ReplaceAllAsync(new[] { "x", "y" }).GetAwaiter().GetResult();
            c.RemoveRange(new[] { "x", "y" });
            Check(count == 3);
        });
        Test("empty unknown enumerable sends no notification", () =>
        {
            var c = New("a");
            int count = 0;
            c.CollectionChanged += (_, _) => count++;
            c.AddRange(Enumerable.Empty<string>().Where(_ => true));
            Check(count == 0);
        });
        Test("Span append grows geometrically", () =>
        {
            var c = New();
            var list = (List<string>)typeof(BulkObservableCollection<string>)
                .GetField("_itemsList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
            int growths = 0;
            for (int i = 0; i < 1000; i++)
            {
                int capacity = list.Capacity;
                c.AddRange(new ReadOnlySpan<string>(new[] { "x" }));
                if (list.Capacity != capacity) growths++;
            }
            Check(c.Count == 1000 && growths < 20);
            Console.WriteLine($"  1000 appends: {growths} capacity growths");
        });
        Test("background synchronous mutation is rejected", () =>
        {
            var c = New("a");
            Throws<InvalidOperationException>(() => Await(Task.Run(() => c.Add("bad"))));
            Equal(c, "a");
        });
        Test("accepted dispatch waits for owner and publishes there", () =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()!;
            var c = New("a");
            c.CollectionChanged += (_, _) => Check(dispatcher.HasThreadAccess);
            var task = Task.Run(() => c.AddRangeAsync(new[] { "b" }));
            Check(SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, 5000));
            Check(!task.IsCompleted);
            Equal(c, "a");
            dispatcher.Drain();
            Await(task);
            Equal(c, "a", "b");
        });
        Test("queued callback failure completes task without modification", () =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()!;
            var c = New("a");
            var task = Task.Run(() => c.ReplaceAllAsync(Broken()));
            Check(SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, 5000));
            dispatcher.Drain();
            Throws<InvalidOperationException>(() => Await(task));
            Equal(c, "a");
        });
        Test("rejected dispatch completes both tasks with error", () =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()!;
            var c = New("a");
            dispatcher.Reject = true;
            try
            {
                Throws<InvalidOperationException>(() => Await(Task.Run(() => c.AddRangeAsync(new[] { "b" }))));
                Throws<InvalidOperationException>(() => Await(Task.Run(() => c.ReplaceAllAsync(new[] { "b" }))));
            }
            finally { dispatcher.Reject = false; }
            Equal(c, "a");
        });
        Test("missing dispatcher reports explicit error", () =>
        {
            var c = Task.Run(() => New("a")).GetAwaiter().GetResult();
            Throws<InvalidOperationException>(() => Await(c.AddRangeAsync(new[] { "b" })));
            Throws<InvalidOperationException>(() => Await(c.ReplaceAllAsync(new[] { "b" })));
            Equal(c, "a");
        });
        Console.WriteLine($"PASS {passed} bulk collection regression scenarios (production source, deterministic dispatcher stub).");
    }

    private static void Test(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    private static BulkObservableCollection<string> New(params string[] values) => new(values);
    private static void Equal(BulkObservableCollection<string> actual, params string[] expected) =>
        Check(actual.SequenceEqual(expected));
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Regression assertion failed.");
    }
    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception("Expected " + typeof(TException).Name);
    }
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static IEnumerable<string> Broken()
    {
        yield return "x";
        throw new InvalidOperationException("input failed");
    }
    private sealed class Once : IEnumerable<string>
    {
        public int Enumerations;
        public IEnumerator<string> GetEnumerator()
        {
            if (++Enumerations != 1) throw new Exception("Enumerated twice.");
            yield return "x";
            yield return "y";
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
