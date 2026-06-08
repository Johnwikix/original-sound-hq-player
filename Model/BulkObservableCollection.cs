using DevWinUI;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

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
            if (items == null) return;

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
            Items.AddRange(items);
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
    }
}