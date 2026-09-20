using System.Diagnostics;
using OriginalSound.Plugin;

namespace OriginalSound.Plugin.Runtime;

/// <summary>One serialized RPC lane per plugin. Cancellation kills the worker because legacy providers may ignore cancellation.</summary>
public sealed class PluginProcess(string hostPath, string manifestPath, string dataPath) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lane = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private Process? _process;
    private Task? _stderr;
    private long _requestId;
    private int _disposed;

    public async Task<PluginReply> InvokeAsync(PluginRequest? request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(25));
        await _lane.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_process is null || _process.HasExited)
            {
                await EndProcessAsync();
                var start = new ProcessStartInfo(hostPath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(manifestPath);
                start.ArgumentList.Add(dataPath);
                _process = Process.Start(start) ?? throw new IOException("Cannot start plugin host.");
                _stderr = DrainErrorsAsync(_process.StandardError);
                var hello = await PluginWire.ReadAsync(_process.StandardOutput.BaseStream, PluginJson.Default.PluginReply, linked.Token);
                if (hello.ApiVersion != 1 || hello.Id != 0 || hello.Result != PluginResult.Found)
                    throw new InvalidDataException("Incompatible plugin host.");
            }
            if (request is null) return new PluginReply { Result = PluginResult.Found };
            request = request with { Id = Interlocked.Increment(ref _requestId) };
            await PluginWire.WriteAsync(_process.StandardInput.BaseStream, request, PluginJson.Default.PluginRequest, linked.Token);
            var reply = await PluginWire.ReadAsync(_process.StandardOutput.BaseStream, PluginJson.Default.PluginReply, linked.Token);
            if (reply.Id != request.Id) throw new InvalidDataException("Mismatched plugin reply.");
            if (reply.ApiVersion != 1 || !Enum.IsDefined(reply.Result) || reply.Lyrics is null ||
                reply.Lyrics.Length > 100 || reply.Lyrics.Any(x => x is null ||
                    string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Text)))
                throw new InvalidDataException("Invalid plugin reply.");
            linked.Token.ThrowIfCancellationRequested();
            return reply;
        }
        catch
        {
            await EndProcessAsync();
            throw;
        }
        finally { _lane.Release(); }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task EndProcessAsync(bool graceful = false)
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (graceful && !process.HasExited)
            {
                try
                {
                    using var budget = new CancellationTokenSource(750);
                    await PluginWire.WriteAsync(process.StandardInput.BaseStream, new PluginRequest { Method = "shutdown" }, PluginJson.Default.PluginRequest, budget.Token);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(budget.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (_stderr is not null) await _stderr.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
        finally { process.Dispose(); _stderr = null; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        await _lane.WaitAsync();
        try { await EndProcessAsync(graceful: true); }
        finally { _lane.Release(); }
        // Do not dispose synchronization objects while callers may still be unwinding cancellation.
    }
}
