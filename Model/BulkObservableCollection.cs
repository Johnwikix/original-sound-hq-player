using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
        private readonly List<T> _itemsList;
        private bool _isMutating;

        private static readonly PropertyChangedEventArgs s_countArgs = new(nameof(Count));
        private static readonly PropertyChangedEventArgs s_indexerArgs = new("Item[]");
        private static readonly NotifyCollectionChangedEventArgs s_resetArgs = new(NotifyCollectionChangedAction.Reset);

        public BulkObservableCollection() => _itemsList = (List<T>)Items;
        public BulkObservableCollection(IEnumerable<T> collection) : base(collection) => _itemsList = (List<T>)Items;
        public BulkObservableCollection(List<T> list) : base(list) => _itemsList = (List<T>)Items;

        // 输入在所属 UI 队列执行时读取；调用方须保持输入稳定直到返回的 Task 完成。
        public Task ReplaceAllAsync(IEnumerable<T> items) =>
            items is null ? Task.CompletedTask : DispatchAsync(() => ExecuteReplaceAll(items));

        public Task AddRangeAsync(IEnumerable<T> items) =>
            items is null ? Task.CompletedTask : DispatchAsync(() => AddRange(items));

        private async Task DispatchAsync(Action action)
        {
            var dispatcher = _dispatcher ?? throw new InvalidOperationException(
                "Async collection updates require construction on a DispatcherQueue thread.");
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
            {
                throw new InvalidOperationException("The collection DispatcherQueue rejected the update.");
            }
            await completion.Task.ConfigureAwait(false);
        }

        private void ExecuteReplaceAll(IEnumerable<T> items)
        {
            using var mutation = BeginMutation();
            var source = PrepareItems(items);
            if (_itemsList.Count == 0 && source.Count == 0) return;
            // 枚举和扩容均在 Clear 前完成，输入失败不会丢失旧内容。
            _itemsList.EnsureCapacity(source.Count);
            _itemsList.Clear();
            _itemsList.AddRange(source);
            RaiseChangeNotifications();
        }

        public void AddRange(IEnumerable<T> items)
        {
            if (items is null) return;
            using var mutation = BeginMutation();
            var source = PrepareItems(items);
            if (source.Count == 0) return;
            _itemsList.AddRange(source);
            RaiseChangeNotifications();
        }

        public void AddRange(ReadOnlySpan<T> source)
        {
            using var mutation = BeginMutation();
            if (source.IsEmpty) return;
            int oldCount = _itemsList.Count;
            int need = checked(oldCount + source.Length);
            _itemsList.EnsureCapacity(need);
            CollectionsMarshal.SetCount(_itemsList, need);
            source.CopyTo(CollectionsMarshal.AsSpan(_itemsList).Slice(oldCount));
            RaiseChangeNotifications();
        }

        public void InsertRange(int index, IEnumerable<T> items)
        {
            using var mutation = BeginMutation();
            ValidateInsertIndex(index);
            if (items is null) return;
            var source = PrepareItems(items);
            if (source.Count == 0) return;
            _ = checked(_itemsList.Count + source.Count);
            _itemsList.InsertRange(index, source);
            RaiseChangeNotifications();
        }

        // 保留现有 WinUI 批量策略：底层先到最终态，再按索引补发逐项事件。
        // 这不是逐项修改的中间态契约；通知期间禁止任何入口重入修改集合。
        public void InsertRangeWithItemNotifications(int index, List<T> items)
        {
            using var mutation = BeginMutation();
            ValidateInsertIndex(index);
            ArgumentNullException.ThrowIfNull(items);
            int count = items.Count;
            if (count == 0) return;
            _ = checked(_itemsList.Count + count);
            _itemsList.InsertRange(index, items);
            for (int i = 0; i < count; i++)
            {
                // 读取已提交的集合，不再借用可能被订阅者修改的输入 List。
                RaiseChangeNotifications(new(NotifyCollectionChangedAction.Add, _itemsList[index + i], index + i));
            }
        }

        public void RemoveRangeWithItemNotifications(int index, int count)
        {
            using var mutation = BeginMutation();
            ValidateRange(index, count);
            if (count == 0) return;
            var removed = _itemsList.GetRange(index, count);
            _itemsList.RemoveRange(index, count);
            foreach (var item in removed)
                RaiseChangeNotifications(new(NotifyCollectionChangedAction.Remove, item, index));
        }

        public void FillFrom(ReadOnlySpan<T> source)
        {
            using var mutation = BeginMutation();
            int count = source.Length;
            if (count > _itemsList.Count)
            {
                _itemsList.EnsureCapacity(count);
                CollectionsMarshal.SetCount(_itemsList, count);
                source.CopyTo(CollectionsMarshal.AsSpan(_itemsList));
            }
            else
            {
                // source 可能是自身切片，缩短 Count 会清除尾部引用，必须先复制。
                source.CopyTo(CollectionsMarshal.AsSpan(_itemsList));
                CollectionsMarshal.SetCount(_itemsList, count);
            }
            RaiseChangeNotifications();
        }

        public void RemoveRange(IEnumerable<T> items)
        {
            if (items is null) return;
            using var mutation = BeginMutation();
            var source = PrepareItems(items);
            if (source.Count == 0) return;

            if (source.Count == 1)
            {
                foreach (var item in source) _itemsList.Remove(item);
            }
            else
            {
                RemoveRangeCore(source);
            }
            RaiseChangeNotifications();
        }

        private void RemoveRangeCore(ICollection<T> source)
        {
#pragma warning disable CS8714 // T may be nullable; null keys are counted separately below.
            var counts = new Dictionary<T, int>(source.Count);
            int nullCount = 0;
            foreach (var item in source)
            {
                if (item is null)
                {
                    nullCount++;
                    continue;
                }
                ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, item, out _);
                count++;
#pragma warning restore CS8714
            }

            int write = 0;
            for (int i = 0; i < _itemsList.Count; i++)
            {
                var item = _itemsList[i];
                if (item is null)
                {
                    if (nullCount > 0)
                    {
                        nullCount--;
                        continue;
                    }
                }
                else if (counts.TryGetValue(item, out int count) && count > 0)
                {
                    counts[item] = count - 1;
                    continue;
                }
                _itemsList[write++] = item;
            }
            if (write < _itemsList.Count)
                _itemsList.RemoveRange(write, _itemsList.Count - write);
        }

        // 借用视图只在下一次集合修改前有效；不得跨异步边界保存。
        public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan(_itemsList);

        public void SortInPlace(IComparer<T> comparer)
        {
            using var mutation = BeginMutation();
            var sorted = _itemsList.ToArray();
            Array.Sort(sorted, comparer);
            sorted.AsSpan().CopyTo(CollectionsMarshal.AsSpan(_itemsList));
            RaiseChangeNotifications();
        }

        private static ICollection<T> PrepareItems(IEnumerable<T> items)
        {
            // List/数组为已物化的独立输入；包括 this 的其他 IEnumerable 先快照，
            // 避免自身别名、单次枚举和枚举异常在修改后才被发现。
            return items is List<T> or T[] ? (ICollection<T>)items : new List<T>(items);
        }

        private void ValidateInsertIndex(int index)
        {
            if ((uint)index > (uint)_itemsList.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        private void ValidateRange(int index, int count)
        {
            if ((uint)index > (uint)_itemsList.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if ((uint)count > (uint)(_itemsList.Count - index))
                throw new ArgumentOutOfRangeException(nameof(count));
        }

        private MutationScope BeginMutation()
        {
            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
                throw new InvalidOperationException("Collection updates must run on their DispatcherQueue thread.");
            if (_isMutating)
                throw new InvalidOperationException("Cannot modify a collection during an update or notification.");
            CheckReentrancy();
            _isMutating = true;
            return new MutationScope(this);
        }

        private readonly struct MutationScope(BulkObservableCollection<T> owner) : IDisposable
        {
            public void Dispose() => owner._isMutating = false;
        }

        // 继承的单项入口也参与同一守卫，防止批量通知回调绕过保护。
        protected override void InsertItem(int index, T item)
        {
            using var mutation = BeginMutation();
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            using var mutation = BeginMutation();
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, T item)
        {
            using var mutation = BeginMutation();
            base.SetItem(index, item);
        }

        protected override void ClearItems()
        {
            using var mutation = BeginMutation();
            base.ClearItems();
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            using var mutation = BeginMutation();
            base.MoveItem(oldIndex, newIndex);
        }

        private void RaiseChangeNotifications() => RaiseChangeNotifications(s_resetArgs);

        private void RaiseChangeNotifications(NotifyCollectionChangedEventArgs args)
        {
            OnPropertyChanged(s_countArgs);
            OnPropertyChanged(s_indexerArgs);
            OnCollectionChanged(args);
        }
    }
}
