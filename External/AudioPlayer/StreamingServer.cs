using System.IO.Pipes;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

namespace AudioPlayer;

/// <summary>Independent bounded control connections keep stop/pause responsive while a source is being prepared.</summary>
internal sealed class StreamingServer(PlaybackEngine engine)
{
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ServeAsync(ct)));
    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(StreamingWire.PipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var command = await StreamingWire.ReadAsync(pipe, StreamingJson.Default.StreamCommand, requestTimeout.Token);
                var reply = engine.HandleStreamCommand(command);
                await StreamingWire.WriteAsync(pipe, reply, StreamingJson.Default.StreamReply, requestTimeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (System.Text.Json.JsonException) { }
            catch (ArgumentException) { }
        }
    }
}
