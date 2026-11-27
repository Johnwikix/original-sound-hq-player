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
    public PlaybackSource? LastSource { get; private set; }
    public Guid Current;
    public PlaybackPeer() => _server = Task.Run(ServeAsync);
    public void FailDecoder() => _phase = StreamPhase.Failed;
    public void SetPosition(long positionMs) => Interlocked.Exchange(ref _positionMs, positionMs);
    private long _positionMs;
    public async Task<byte[]> ReadAsync(long start, long end)
        => (await RequestAsync(start, end)).Bytes;

    public async Task<(int Status, byte[] Bytes)> RequestAsync(long start, long end)
    {
        string location;
        lock (_locations) location = _locations[Current];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.Range = new(start, end);
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private Task ServeAsync()
        => new PipeCommandServer(StreamingWire.PipeName, Guid.NewGuid(), Handle, instances: 4).RunAsync(_stop.Token);

    private PipeResponse Handle(CommandId id, ReadOnlySpan<byte> payload)
    {
        var command = System.Text.Json.JsonSerializer.Deserialize(payload, StreamingJson.Default.StreamCommand)!;
        lock (_locations)
        {
            if (command.Method == "prepare")
            {
                _locations[command.SessionId] = command.Source!.Location;
                LastSource = command.Source;
                Current = command.SessionId;
                _phase = StreamPhase.Playing;
                Prepares++;
                LastPreparePositionMs = command.PositionMs;
            }
            if (command.Method == "pause") _phase = StreamPhase.Paused;
            if (command.Method == "play") _phase = StreamPhase.Playing;
            var reply = new StreamReply
            {
                RequestId = command.RequestId, SessionId = command.SessionId, Accepted = true,
                Phase = _phase, WantsPlay = _phase == StreamPhase.Playing, DurationMs = 60000,
                CanSeek = LastSource?.CanSeek == true,
                PositionMs = command.Method == "prepare" ? command.PositionMs : Interlocked.Read(ref _positionMs)
            };
            return new(MessageTypeId.Success, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reply, StreamingJson.Default.StreamReply));
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _server; _stop.Dispose(); }
}
