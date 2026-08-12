using DevWinUI;
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
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

        public BulkObservableCollection() { }

        public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }

        public BulkObservableCollection(List<T> list) : base(list) { }

        /// <summary>
        /// 异步将集合内容更新为新集合（并集/替换），仅触发一次通知
        /// </summary>
        public async Task ReplaceAllAsync(IEnumerable<T> items)
        {
            if (items == null) return;

            if (_dispatcher.HasThreadAccess)
            {
                ExecuteReplaceAll(items);
                return;
            }

            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ExecuteReplaceAll(items);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            await tcs.Task;
        }

        private void ExecuteReplaceAll(IEnumerable<T> items)
        {
            // 1. 检查数据是否真的变了（防止无效刷新）
            // 如果你传入的是同一个引用且数量一致，可以选择跳过
            var newList = items;

            // 2. 悄悄修改内部原始列表
            this.Items.Clear();
            Items.AddRange(items);  
            // 3. 统一触发 UI 通知
            // 这里使用 Reset 动作，因为 Items 已经完全变了
            RaiseChangeNotifications();
        }

        /// <summary>
        /// 异步批量添加
        /// </summary>
        public async Task AddRangeAsync(IEnumerable<T> items)
        {
            if (items == null || IsKnownEmpty(items)) return;

            if (_dispatcher.HasThreadAccess)
            {
                ExecuteAddRange(items);
                return;
            }

            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ExecuteAddRange(items);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            await tcs.Task;
        }

        private void ExecuteAddRange(IEnumerable<T> items)
        {
            if (items == null || IsKnownEmpty(items)) return;
            Items.AddRange(items);
            RaiseChangeNotifications();
        }

        public void AddRange(IEnumerable<T> items)
        {
            ExecuteAddRange(items);
        }

        public void FillFrom(ReadOnlySpan<T> source)
        {
            var list = (List<T>)Items;
            list.Clear();
            if (list.Capacity < source.Length)
                list.Capacity = source.Length;
            foreach (var item in source)
                list.Add(item);
            RaiseChangeNotifications();
        }

        public void RemoveRange(IEnumerable<T> items)
        {
            if (items == null) return;

            var snapshot = items as ICollection<T>;
            if (snapshot == null)
            {
                var copy = new List<T>();
                foreach (var item in items)
                {
                    copy.Add(item);
                }
                snapshot = copy;
            }

            if (snapshot.Count == 0) return;

            if (snapshot.Count == 1)
            {
                foreach (var item in snapshot)
                {
                    Items.Remove(item);
                }
            }
            else
            {
                RemoveRangeCore(snapshot);
            }

            RaiseChangeNotifications();
        }

        private void RemoveRangeCore(ICollection<T> toRemove)
        {
            var counts = new Dictionary<T, int>(toRemove.Count);
            foreach (var item in toRemove)
            {
                counts.TryGetValue(item, out var count);
                counts[item] = count + 1;
            }

            var itemsList = (List<T>)Items;
            int write = 0;
            for (int i = 0; i < itemsList.Count; i++)
            {
                var item = itemsList[i];
                if (counts.TryGetValue(item, out var count) && count > 0)
                {
                    counts[item] = count - 1;
                }
                else
                {
                    itemsList[write++] = item;
                }
            }
            if (write < itemsList.Count)
            {
                itemsList.RemoveRange(write, itemsList.Count - write);
            }
        }

        public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan((List<T>)Items);

        public void SortInPlace(IComparer<T> comparer)
        {
            CollectionsMarshal.AsSpan((List<T>)Items).Sort(comparer);
            RaiseChangeNotifications();
        }

        /// <summary>
        /// 统一触发 Count 和 Reset 通知
        /// </summary>
        private void RaiseChangeNotifications()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// 仅通过 Count 判断空集合，不做枚举（避免消费一次性 IEnumerable）
        /// </summary>
        private static bool IsKnownEmpty(IEnumerable<T> items) =>
            items is IReadOnlyCollection<T> { Count: 0 };
    }
}