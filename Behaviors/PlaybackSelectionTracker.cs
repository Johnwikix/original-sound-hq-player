using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using Windows.Foundation.Collections;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Behaviors;

/// <summary>仅同步播放选择，不滚动、不聚焦、不改变队列；所有外部订阅只在列表加载期间存在。</summary>
public sealed class PlaybackSelectionTracker
{
    private readonly ListViewBase _list;
    private readonly AppViewModel _app;
    private readonly bool _queue;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueHandler _processPending;
    private readonly DispatcherQueueHandler _checkUnloaded;
    private volatile bool _active;
    private bool _synchronizing;
    private int _syncQueued;

    public PlaybackSelectionTracker(ListViewBase list, AppViewModel app, bool queue = false)
    {
        _list = list;
        _app = app;
        _queue = queue;
        _dispatcher = list.DispatcherQueue;
        _processPending = ProcessPending;
        _checkUnloaded = CheckUnloaded;
        list.Loaded += Loaded;
        list.Unloaded += Unloaded;
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        _app.PropertyChanged += AppChanged;
        _list.Items.VectorChanged += ItemsChanged;
        if (_queue) _app.State.Queue.PropertyChanged += QueueChanged;
        Synchronize();
    }

    private void Unloaded(object sender, RoutedEventArgs args)
    {
        // 旧 Unloaded 可能在重新 Loaded 后到达，不能解绑新一轮活动订阅。
        if (_list.IsLoaded) _dispatcher.TryEnqueue(_checkUnloaded);
        else CheckUnloaded();
    }

    private void CheckUnloaded()
    {
        if (_list.IsLoaded || !_active) return;
        _active = false;
        _app.PropertyChanged -= AppChanged;
        _list.Items.VectorChanged -= ItemsChanged;
        if (_queue) _app.State.Queue.PropertyChanged -= QueueChanged;
    }

    private void AppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.SelectedPlaybackMusic) or nameof(AppViewModel.CurrentPlayingMusic)
            or nameof(AppViewModel.CurrentPlayingList)) Synchronize();
    }

    private void QueueChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == "Version") Synchronize();
    }

    private void ItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs args) => QueueSynchronization();

    private void QueueSynchronization()
    {
        if (!_active || Interlocked.CompareExchange(ref _syncQueued, 1, 0) != 0) return;
        if (!_dispatcher.TryEnqueue(_processPending)) Interlocked.Exchange(ref _syncQueued, 0);
    }

    private void ProcessPending()
    {
        Interlocked.Exchange(ref _syncQueued, 0);
        // 不捕获音乐、集合或索引快照；重载后也只读取当前状态，离树后不触碰选择。
        Synchronize();
    }

    private void Synchronize()
    {
        if (!_active) return;
        if (!_dispatcher.HasThreadAccess)
        {
            QueueSynchronization();
            return;
        }
        if (!_list.IsLoaded || _synchronizing) return;
        _synchronizing = true;
        try
        {
            if (_queue)
            {
                int index = _app.GetSelectedPlaybackIndex();
                if (index >= 0 && index < _list.Items.Count && _list.SelectedIndex != index) _list.SelectedIndex = index;
                return;
            }
            if (_app.SelectedPlaybackMusic is not { } current) return;
            object? match = null;
            foreach (var item in _list.Items)
            {
                var music = item is PlayListMusicItem wrapper ? wrapper.Music : item as Music;
                if (music is null) continue;
                if (ReferenceEquals(music, current)) { match = item; break; }
                if (match is null && CurrentTrackLookup.Matches(music, current)) match = item;
            }
            if (match is not null && !ReferenceEquals(_list.SelectedItem, match)) _list.SelectedItem = match;
        }
        finally { _synchronizing = false; }
    }
}
