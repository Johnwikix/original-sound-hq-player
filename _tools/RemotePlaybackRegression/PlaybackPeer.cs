using System.IO.Pipes;
using BassPlayerIpc.Shared;

// Deterministic decoder peer on a private real pipe. It fetches the real loopback bridge while
// intentionally continuing to report Playing after a broken read, like a decoder with buffered PCM.
internal sealed class PlaybackPeer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly Dictionary<Guid, string> _locations = [];
    private StreamPhase _phase = StreamPhase.Playing;
    public int Prepares;
    public long LastPreparePositionMs;
    public Guid Current;
    public PlaybackPeer() => _server = Task.Run(ServeAsync);
    public void FailDecoder() => _phase = StreamPhase.Failed;
    public void SetPosition(long positionMs) => Interlocked.Exchange(ref _positionMs, positionMs);
    private long _positionMs;
    public async Task<byte[]> ReadAsync(long start, long end)
    {
        string location;
        lock (_locations) location = _locations[Current];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.Range = new(start, end);
        using var response = await http.SendAsync(request);
        return await response.Content.ReadAsByteArrayAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(StreamingWire.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var command = await StreamingWire.ReadAsync(pipe, StreamingJson.Default.StreamCommand, _stop.Token);
                    if (command.Method == "prepare")
                    {
                        lock (_locations) _locations[command.SessionId] = command.Source!.Location;
                        Current = command.SessionId;
                        _phase = StreamPhase.Playing;
                        Prepares++;
                        LastPreparePositionMs = command.PositionMs;
                    }
                    if (command.Method == "pause") _phase = StreamPhase.Paused;
                    if (command.Method == "play") _phase = StreamPhase.Playing;
                    await StreamingWire.WriteAsync(pipe, new StreamReply
                    {
                        RequestId = command.RequestId, SessionId = command.SessionId, Accepted = true,
                        Phase = _phase, WantsPlay = _phase == StreamPhase.Playing, DurationMs = 60000,
                        PositionMs = command.Method == "prepare" ? command.PositionMs : Interlocked.Read(ref _positionMs)
                    }, StreamingJson.Default.StreamReply, _stop.Token);
                }
                catch (IOException) { /* A canceled IPC request may close its pipe before the reply. */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _server; _stop.Dispose(); }
}
