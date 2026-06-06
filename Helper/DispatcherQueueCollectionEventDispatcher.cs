using Microsoft.UI.Dispatching;
using ObservableCollections;
using System;

namespace WinUIMusicPlayer.Helper
{
    public sealed class DispatcherQueueCollectionEventDispatcher : ICollectionEventDispatcher
    {
        private readonly DispatcherQueue _dispatcherQueue;

        private DispatcherQueueCollectionEventDispatcher(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
        }

        public static DispatcherQueueCollectionEventDispatcher? Instance { get; private set; }

        public static void Initialize(DispatcherQueue dispatcherQueue)
        {
            Instance = new DispatcherQueueCollectionEventDispatcher(dispatcherQueue);
        }

        public void Post(CollectionEventDispatcherEventArgs args)
        {
            _dispatcherQueue.TryEnqueue(() => args.Invoke());
        }
    }
}
