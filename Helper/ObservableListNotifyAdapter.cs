using ObservableCollections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinUIMusicPlayer.Helper
{
    /// <summary>
    /// Bridges an <see cref="ObservableList{T}"/> to WinUI's INotifyCollectionChanged contract,
    /// forwarding every source mutation (Add/Remove/Replace/Move/Reset) as a corresponding
    /// System NotifyCollectionChangedEventArgs. Implements IList&lt;T&gt;.Move so ListView reorder
    /// gestures work.
    /// </summary>
    /// <remarks>
    /// Bulk-reorder operations (ShuffleInPlace, RestoreOrder) run with internal events
    /// suppressed and emit a single Reset afterwards. Emitting N Move events to a WinUI
    /// ListView causes N independent layout passes (the entire panel re-arranges per Move),
    /// which is dramatically slower than one Reset-triggered full rebuild.
    /// </remarks>
    public sealed class ObservableListNotifyAdapter<T> : INotifyCollectionChanged, INotifyPropertyChanged,
        IList<T>, IReadOnlyList<T>, IReadOnlyCollection<T>
    {
        private readonly ObservableList<T> _list;
        private NotifyCollectionChangedEventHandler? _collectionChanged;
        private PropertyChangedEventHandler? _propertyChanged;
        private bool _suppressEvents;

        public ObservableListNotifyAdapter(ObservableList<T> list)
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _list.CollectionChanged += OnSourceCollectionChanged;
        }

        public ObservableList<T> Source => _list;

        public T this[int index] { get => _list[index]; set => _list[index] = value; }
        public int Count => _list.Count;
        public bool IsReadOnly => false;
        public bool IsFixedSize => false;
        public bool IsSynchronized => false;
        public object SyncRoot => _list.SyncRoot ?? this;

        public void Add(T item) => _list.Add(item);
        public void Insert(int index, T item) => _list.Insert(index, item);
        public bool Remove(T item) => _list.Remove(item);
        public void RemoveAt(int index) => _list.RemoveAt(index);
        public void Clear() => _list.Clear();
        public bool Contains(T item) => _list.Contains(item);
        public int IndexOf(T item) => _list.IndexOf(item);
        public void Move(int oldIndex, int newIndex) => _list.Move(oldIndex, newIndex);

        public void CopyTo(T[] array, int arrayIndex)
        {
            for (int i = 0; i < _list.Count; i++) array[arrayIndex + i] = _list[i];
        }

        public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => _collectionChanged += value;
            remove => _collectionChanged -= value;
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _propertyChanged += value;
            remove => _propertyChanged -= value;
        }

        public void ShuffleInPlace(Random rng)
        {
            int n = _list.Count;
            _suppressEvents = true;
            try
            {
                while (n > 1)
                {
                    n--;
                    int k = rng.Next(n + 1);
                    if (k != n) _list.Move(n, k);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
            FireReset();
        }

        public void RestoreOrder(IReadOnlyList<T> snapshot)
        {
            if (snapshot == null || _list.Count != snapshot.Count) return;
            _suppressEvents = true;
            try
            {
                var currentPositions = new Dictionary<T, int>(_list.Count);
                for (int i = 0; i < _list.Count; i++) currentPositions[_list[i]] = i;
                for (int targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
                {
                    T item = snapshot[targetIndex];
                    int currentIndex = currentPositions[item];
                    if (currentIndex != targetIndex)
                    {
                        _list.Move(currentIndex, targetIndex);
                        var displaced = _list[targetIndex];
                        currentPositions[displaced] = currentIndex;
                        currentPositions[item] = targetIndex;
                    }
                }
            }
            finally
            {
                _suppressEvents = false;
            }
            FireReset();
        }

        private void FireReset()
        {
            var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
            DispatchIfNeeded(() =>
            {
                _collectionChanged?.Invoke(this, args);
                _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                _propertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            });
        }

        private void OnSourceCollectionChanged(in NotifyCollectionChangedEventArgs<T> e)
        {
            if (_suppressEvents) return;
            var sysArgs = ConvertToSystemArgs(in e);
            if (sysArgs == null) return;
            DispatchIfNeeded(() =>
            {
                _collectionChanged?.Invoke(this, sysArgs);
                _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                _propertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            });
        }

        private static void DispatchIfNeeded(Action action)
        {
            var dq = App.MainWindow?.DispatcherQueue;
            if (dq == null || dq.HasThreadAccess)
            {
                action();
                return;
            }
            dq.TryEnqueue(() => action());
        }

        private static NotifyCollectionChangedEventArgs? ConvertToSystemArgs(in NotifyCollectionChangedEventArgs<T> e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.IsSingleItem) return new NotifyCollectionChangedEventArgs(e.Action, e.NewItem, e.NewStartingIndex);
                    return new NotifyCollectionChangedEventArgs(e.Action, e.NewItems.ToArray(), e.NewStartingIndex);
                case NotifyCollectionChangedAction.Remove:
                    if (e.IsSingleItem) return new NotifyCollectionChangedEventArgs(e.Action, e.OldItem, e.OldStartingIndex);
                    return new NotifyCollectionChangedEventArgs(e.Action, e.OldItems.ToArray(), e.OldStartingIndex);
                case NotifyCollectionChangedAction.Replace:
                    if (e.IsSingleItem) return new NotifyCollectionChangedEventArgs(e.Action, e.NewItem, e.OldItem, e.NewStartingIndex);
                    return new NotifyCollectionChangedEventArgs(e.Action, e.NewItems.ToArray(), e.OldItems.ToArray(), e.NewStartingIndex);
                case NotifyCollectionChangedAction.Move:
                    if (e.IsSingleItem) return new NotifyCollectionChangedEventArgs(e.Action, e.NewItem, e.NewStartingIndex, e.OldStartingIndex);
                    return new NotifyCollectionChangedEventArgs(e.Action, e.NewItems.ToArray(), e.NewStartingIndex, e.OldStartingIndex);
                case NotifyCollectionChangedAction.Reset:
                    return new NotifyCollectionChangedEventArgs(e.Action);
                default:
                    return null;
            }
        }
    }
}
