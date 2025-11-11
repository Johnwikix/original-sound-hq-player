using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Extensions
{
    public static class DispatcherQueueExtensions
    {
        public static async Task EnqueueAsync(this DispatcherQueue dispatcher, Action action)
        {
            var taskCompletionSource = new TaskCompletionSource<bool>();

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    taskCompletionSource.SetResult(true);
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            }))
            {
                taskCompletionSource.SetException(new InvalidOperationException("Failed to enqueue task"));
            }

            await taskCompletionSource.Task;
        }
    }
}
