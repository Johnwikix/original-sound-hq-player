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
        private readonly List<T> _itemsList;

        public BulkObservableCollection() => _itemsList = (List<T>)Items;

        public BulkObservableCollection(IEnumerable<T> collection) : base(collection)
            => _itemsList = (List<T>)Items;

        public BulkObservableCollection(List<T> list) : base(list)
            => _itemsList = (List<T>)Items;

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
            _itemsList.Clear();
            _itemsList.AddRange(items);
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
            _itemsList.AddRange(items);
            RaiseChangeNotifications();
        }

        public void AddRange(IEnumerable<T> items)
        {
            ExecuteAddRange(items);
        }

        public void InsertRange(int index, IEnumerable<T> items)
        {
            if (items is null) return;

            if (items is ICollection<T> coll)
            {
                if (coll.Count == 0) return;
                InsertRangeCore(_itemsList, index, coll);
                RaiseChangeNotifications();
                return;
            }

            _itemsList.InsertRange(index, items);
            RaiseChangeNotifications();
        }

        private static void InsertRangeCore(List<T> dst, int index, ICollection<T> src)
        {
            int oldCount = dst.Count;
            int insertCount = src.Count;
            int need = oldCount + insertCount;
            if (dst.Capacity < need) dst.Capacity = need;

            CollectionsMarshal.SetCount(dst, need);
            var span = CollectionsMarshal.AsSpan(dst);
            span.Slice(index, oldCount - index).CopyTo(span.Slice(index + insertCount));

            var target = span.Slice(index, insertCount);
            switch (src)
            {
                case T[] arr:
                    arr.AsSpan().CopyTo(target);
                    break;
                case List<T> srcList:
                    CollectionsMarshal.AsSpan(srcList).CopyTo(target);
                    break;
                default:
                    int i = 0;
                    foreach (var item in src)
                    {
                        target[i++] = item;
                    }
                    break;
            }
        }

        public void FillFrom(ReadOnlySpan<T> source)
        {
            var list = _itemsList;
            if (list.Capacity < source.Length)
                list.Capacity = source.Length;
            CollectionsMarshal.SetCount(list, source.Length);
            source.CopyTo(CollectionsMarshal.AsSpan(list));
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
                    _itemsList.Remove(item);
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

            var itemsList = _itemsList;
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

        public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan(_itemsList);

        public void SortInPlace(IComparer<T> comparer)
        {
            CollectionsMarshal.AsSpan(_itemsList).Sort(comparer);
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