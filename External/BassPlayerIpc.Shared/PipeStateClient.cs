using System.IO.Pipes;

namespace BassPlayerIpc.Shared;

/// <summary>Background pipe reader publishes complete snapshots; UI reads only this local cache.</summary>
public sealed class PipeStateClient : IDisposable, IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task _reader = Task.CompletedTask;
    private ProgressSnapshot _progress;
    private DspStateSnapshot? _dsp;
    private FftSnapshot? _fft;
    private bool _started, _stopped;
    private int _resourcesDisposed;
    public Guid InstanceId { get; }
    public DspStateSnapshot? CurrentDspState => Volatile.Read(ref _dsp);
    public FftSnapshot? CurrentFftSnapshot => Volatile.Read(ref _fft);
    public event Action<DspStateSnapshot>? DspStateChanged;
    /// <summary>Raised on the pipe reader thread. The snapshot is immutable by convention.</summary>
    public event Action<FftSnapshot>? FftDataChanged;
    /// <summary>Payload is borrowed only for the duration of this callback.</summary>
    public event Action<MessageTypeId, ReadOnlyMemory<byte>>? NotificationReceived;
    public event Action<Exception>? Faulted;

    private PipeStateClient(NamedPipeClientStream pipe, Guid instanceId) { _pipe = pipe; InstanceId = instanceId; }

    public static async Task<PipeStateClient> ConnectAsync(string name, CancellationToken token = default)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var instanceId = await PipeProtocol.ConnectAsync(pipe, token).ConfigureAwait(false);
            return new(pipe, instanceId);
        }
        catch { pipe.Dispose(); throw; }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) return;
            _started = true;
            _reader = Task.Run(ReadAsync);
        }
    }

    public bool TryGetProgress(out ProgressSnapshot snapshot)
    {
        lock (_gate) { snapshot = _progress; return !_stopped && snapshot.Revision > 0; }
    }

    private async Task ReadAsync()
    {
        using var reader = new PipeFrameReader(IpcConstants.MaxStatePayloadSize);
        try
        {
            while (true)
            {
                var frame = await reader.ReadAsync(_pipe, _stop.Token).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case PipeFrameKind.Progress:
                        var progress = ProgressProtocol.Read(frame.Payload.Span);
                        if (frame.Id != progress.Revision || frame.Id <= 0) throw new InvalidDataException("Invalid progress revision.");
                        lock (_gate)
                        {
                            if (progress.Revision > _progress.Revision) _progress = progress;
                        }
                        break;
                    case PipeFrameKind.DspState:
                        if (frame.Id <= 0 || frame.Payload.Length != DspProtocol.StateSize) throw new InvalidDataException("Invalid DSP snapshot.");
                        if (frame.Id <= (CurrentDspState?.Revision ?? 0)) break;
                        var dsp = new DspStateSnapshot(frame.Id, DspProtocol.ReadState(frame.Payload.Span));
                        Volatile.Write(ref _dsp, dsp);
                        DspStateChanged?.Invoke(dsp);
                        break;
                    case PipeFrameKind.FftData:
                        var fft = FftProtocol.Read(frame.Payload.Span);
                        if (fft.Sequence <= (CurrentFftSnapshot?.Sequence ?? 0)) break;
                        Volatile.Write(ref _fft, fft);
                        FftDataChanged?.Invoke(fft);
                        break;
                    case PipeFrameKind.Notification:
                        if (frame.Type is < short.MinValue or > short.MaxValue) throw new InvalidDataException("Invalid notification type.");
                        NotificationReceived?.Invoke((MessageTypeId)frame.Type, frame.Payload);
                        break;
                    default: throw new InvalidDataException("Unexpected audio state frame.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                lock (_gate) _stopped = true;
                try { Faulted?.Invoke(ex); } catch { }
            }
        }
        finally { _pipe.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task reader;
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true;
                _stop.Cancel();
                _pipe.Dispose();
            }
            reader = _reader;
        }
        await reader.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0) _stop.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
