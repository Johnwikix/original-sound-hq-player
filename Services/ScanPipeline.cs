using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>
/// Fixed worker count and bounded results; one consumer owns each batch until its callback completes.
/// No task per file, directory-wide barrier, or detached UI work. A failed consumer cancels producers.
/// </summary>
internal static class ScanPipeline
{
    internal const int BatchSize = 128;
    internal const int WorkerCount = 4;

    internal static async Task RunAsync<TInput, TResult>(IEnumerable<TInput> source,
        Func<TInput, CancellationToken, ValueTask<TResult>> read,
        Func<IReadOnlyList<TResult>, Task> consume,
        TimeSpan interval, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<TResult>(new BoundedChannelOptions(BatchSize)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(source, new ParallelOptions
                {
                    MaxDegreeOfParallelism = WorkerCount,
                    CancellationToken = stop.Token
                }, async (item, token) =>
                {
                    var result = await read(item, token).ConfigureAwait(false);
                    await channel.Writer.WriteAsync(result, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, CancellationToken.None);

        var batch = new List<TResult>(BatchSize);
        long lastFlush = 0;
        try
        {
            while (await channel.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                if (lastFlush != 0)
                {
                    var delay = interval - Stopwatch.GetElapsedTime(lastFlush);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                }
                while (batch.Count < BatchSize && channel.Reader.TryRead(out var result))
                    batch.Add(result);
                await consume(batch).ConfigureAwait(false);
                batch.Clear(); // Callback must finish using the batch before returning.
                lastFlush = Stopwatch.GetTimestamp();
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await producer.ConfigureAwait(false); }
            catch when (!cancellationToken.IsCancellationRequested) { /* Preserve the consumer/enumeration exception. */ }
        }
    }
}
