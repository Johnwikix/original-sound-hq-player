using BassPlayerIpc.Shared;
using System.Collections.Concurrent;

namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    private sealed class StreamSlot(Guid id, PlaybackSource source)
    {
        public readonly Guid Id = id;
        public readonly PlaybackSource Source = source;
        public readonly CancellationTokenSource Cancel = new();
        public Task Work = Task.CompletedTask;
        public Session? Prepared;
        public StreamPhase Phase = StreamPhase.Opening;
        public bool WantsPlay;
        public long ResumePosition;
        public long SeekId;
        public string? Error;
    }
    private readonly ConcurrentDictionary<Guid, StreamSlot> _streamSlots = new();
    private volatile bool _streamingStopping;
    private Guid _currentStream;
    private Guid _requestedStream;
    private readonly ConcurrentDictionary<Task, byte> _streamWork = new();

    private readonly object _streamCommandLock = new();

    public StreamReply HandleStreamCommand(StreamCommand command)
    {
        // Stop must interrupt native seek/recovery before waiting for either control lock.
        if (command.Version == 1 && command.Method == "stop" &&
            _streamSlots.TryGetValue(command.SessionId, out var stopping))
        {
            try { stopping.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        }
        // Serialize source replacement validation and cancellation.
        // The watchdog uses only _streamLock.
        lock (_streamCommandLock) return HandleStreamCommandCore(command);
    }

    private StreamReply HandleStreamCommandCore(StreamCommand command)
    {
        if (command.Version != 1) return Reject(command, "ProtocolVersion");
        // Interrupt native I/O before acquiring the engine lock; recovery may currently be opening a source.
        if (command.Method == "refresh")
        {
            if (_streamWork.Count >= 2) return Reject(command, "PreparationLimit");
            if (!_streamSlots.TryGetValue(command.SessionId, out var previous) || command.Source is null ||
                command.Source.ResourceId != previous.Source.ResourceId) return Reject(command, "ResourceMismatch");
            try { command.Source.Validate(); }
            catch { return Reject(command, "InvalidSource"); }
            if (command.Source.IsLive) return Reject(command, "UnsupportedLiveSource");
        }
        if (command.Method is "stop" or "refresh" && _streamSlots.TryGetValue(command.SessionId, out var stopping))
        {
            try { stopping.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        }
        lock (_streamLock)
        {
            if (_disposed != 0 || _streamingStopping) return Reject(command, "Stopping");
            if (command.Method == "capabilities") return new()
            {
                RequestId = command.RequestId, Accepted = true,
                Capabilities = ["local-file", "http-pcm", "prepare", "pause-during-prepare", "bounded-preload", "seek", "source-refresh", "buffer-status"]
            };
            if (command.Method == "prepare")
            {
                if (command.SessionId == Guid.Empty || command.Source is null) return Reject(command, "InvalidSource");
                if (_streamSlots.ContainsKey(command.SessionId)) return Reject(command, "DuplicateSession");
                if (_streamSlots.Count >= 2 || _streamWork.Count >= 2) return Reject(command, "PreparationLimit");
                try { command.Source.Validate(); }
                catch { return Reject(command, "InvalidSource"); }
                if (command.Source.IsLive) return Reject(command, "UnsupportedLiveSource");
                var created = new StreamSlot(command.SessionId, command.Source);
                _streamSlots[created.Id] = created;
                BeginPreparation(created);
                return Snapshot(command, created);
            }
            if (!_streamSlots.TryGetValue(command.SessionId, out var slot)) return Reject(command, "UnknownSession");
            switch (command.Method)
            {
                case "status": break;
                case "play":
                    Snapshot(command, slot);
                    if (slot.Phase == StreamPhase.Failed) return Reject(command, slot.Error ?? "SourceFailed");
                    _requestedStream = slot.Id;
                    foreach (var other in _streamSlots.Values) if (other.Id != slot.Id) other.WantsPlay = false;
                    slot.WantsPlay = true;
                    if (_currentStream == slot.Id)
                    {
                        if (slot.Phase == StreamPhase.Ended) { slot.WantsPlay = false; return Reject(command, "SeekOrPrepareRequired"); }
                        ResumeCore();
                    }
                    // A prepared seek may still be refilling. The ring gates output until its
                    // threshold is reached; play intent must not depend on a later status poll.
                    else if (slot.Prepared is not null) CommitStream(slot);
                    break;
                case "pause":
                    slot.WantsPlay = false;
                    if (_currentStream == slot.Id) PauseCore();
                    break;
                case "stop":
                    RemoveStream(slot);
                    return new() { Accepted = true, SessionId = slot.Id, RequestId = command.RequestId, Phase = StreamPhase.Stopped };
                case "seek":
                    var seekSession = _currentStream == slot.Id ? _session : slot.Prepared;
                    if (!slot.Source.CanSeek || seekSession is not { CanSeek: true } || command.PositionMs < 0 || command.PositionMs > long.MaxValue / 1000 ||
                        (seekSession.TotalMs > 0 && command.PositionMs > seekSession.TotalMs))
                        return Reject(command, "SeekUnavailable");
                    if (command.SeekId <= slot.SeekId) return Snapshot(command, slot);
                    slot.SeekId = command.SeekId;
                    slot.ResumePosition = command.PositionMs;
                    seekSession.RequestSeek(command.PositionMs, command.SeekId);
                    if (_currentStream == slot.Id) _lastProgressSeekId = command.SeekId;
                    slot.Phase = StreamPhase.Buffering;
                    break;
                case "refresh":
                    if (_streamWork.Count >= 2) return Reject(command, "PreparationLimit");
                    if (command.Source is null || command.Source.ResourceId != slot.Source.ResourceId) return Reject(command, "ResourceMismatch");
                    try { command.Source.Validate(); }
                    catch { return Reject(command, "InvalidSource"); }
                    if (command.Source.IsLive) return Reject(command, "UnsupportedLiveSource");
                    var replacement = new StreamSlot(slot.Id, command.Source)
                    {
                        WantsPlay = slot.WantsPlay,
                        ResumePosition = _currentStream == slot.Id ? GetTimeProgress().Item1 : slot.ResumePosition,
                        SeekId = slot.SeekId
                    };
                    RemoveStream(slot);
                    _streamSlots[replacement.Id] = replacement;
                    if (replacement.WantsPlay) _requestedStream = replacement.Id;
                    BeginPreparation(replacement);
                    return Snapshot(command, replacement);
                default: return Reject(command, "UnsupportedCommand");
            }
            return Snapshot(command, slot);
        }
    }

    private void BeginPreparation(StreamSlot slot)
    {
        int dsdRate = DsdPcmFreq, dsdGain = DsdGain, latency = Latency;
        slot.Work = Task.Run(async () =>
        {
            Session? prepared = null;
            try
            {
                prepared = Session.Open(this, slot.Source.Location, RenderKind.Pcm, dsdRate, dsdGain, latency,
                    maxChannels: 2, source: slot.Source, cancellationToken: slot.Cancel.Token);
                if (prepared is null) throw new IOException("OpenFailed");
                slot.Cancel.Token.ThrowIfCancellationRequested();
                if (slot.ResumePosition > 0)
                {
                    if (!prepared.CanSeek) throw new IOException("ResumeUnavailable");
                    prepared.RequestSeek(slot.ResumePosition, slot.SeekId);
                }
                lock (_streamLock) slot.Phase = StreamPhase.Buffering;
                long deadline = Environment.TickCount64 + slot.Source.Buffer.OpenTimeoutMs;
                while (!prepared.InputEnded && prepared.ReadyFrames < prepared.InitialBufferFrames)
                {
                    if (prepared.DecodeFailure is not null) throw new IOException("ReadFailed");
                    if (Environment.TickCount64 >= deadline) throw new IOException("BufferTimeout");
                    await Task.Delay(20, slot.Cancel.Token).ConfigureAwait(false);
                }
                if (prepared.DecodeFailure is not null) throw new IOException("ReadFailed");
                if (prepared.ReadyFrames == 0) throw new IOException("EmptySource");
                lock (_streamLock)
                {
                    slot.Cancel.Token.ThrowIfCancellationRequested();
                    if (_disposed != 0 || !_streamSlots.TryGetValue(slot.Id, out var current) || !ReferenceEquals(slot, current)) return;
                    slot.Prepared = prepared;
                    prepared = null; // ownership transfers to slot, then to the engine when committed
                    slot.Phase = StreamPhase.Ready;
                    if (slot.WantsPlay && _requestedStream == slot.Id) CommitStream(slot);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                lock (_streamLock) { slot.Phase = StreamPhase.Failed; slot.Error = "OpenOrReadFailed"; }
            }
            finally { prepared?.Dispose(); }
        });
        _streamWork.TryAdd(slot.Work, 0);
        _ = ObservePreparationAsync(slot.Work);
    }

    private async Task ObservePreparationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { _streamWork.TryRemove(task, out _); }
    }

    private void CommitStream(StreamSlot slot)
    {
        if (slot.Prepared is null || slot.Cancel.IsCancellationRequested) return;
        Interlocked.Increment(ref _playGen);
        Interlocked.Increment(ref _pauseFadeToken);
        _recovery = null;
        if (_currentStream != Guid.Empty && _streamSlots.TryGetValue(_currentStream, out var old)) RemoveStream(old);
        DisposeSession();
        _currentStream = slot.Id;
        MusicUrl = slot.Source.Location;
        SetSession(slot.Prepared);
        slot.Prepared = null;
        ResetAtmosAttempt();
        ResetSurroundAttempt();
        _session!.ConfigureDsp(ResolveDsp(_output?.DeviceId));
        StartOutputAndPlay();
        slot.Phase = IsPlaying ? StreamPhase.Playing : StreamPhase.Failed;
        if (!IsPlaying) slot.Error = "OutputFailed";
    }

    private void RemoveStream(StreamSlot slot)
    {
        slot.WantsPlay = false;
        slot.Cancel.Cancel();
        if (_currentStream == slot.Id)
        {
            Interlocked.Increment(ref _pauseFadeToken);
            Interlocked.Increment(ref _playGen);
            _recovery = null;
            _currentStream = Guid.Empty;
            MusicUrl = string.Empty;
            DisposeSession();
            IsPlaying = false;
            _ipc.PlayStateUpdate(false);
        }
        slot.Prepared?.Dispose();
        slot.Prepared = null;
        slot.Phase = StreamPhase.Stopped;
        _streamSlots.TryRemove(slot.Id, out _);
        if (_requestedStream == slot.Id) _requestedStream = Guid.Empty;
        // Cancellation source remains valid until the preparation task has released all native callbacks.
        _ = DisposeSlotTokenAsync(slot);
    }

    private static async Task DisposeSlotTokenAsync(StreamSlot slot)
    {
        try { await slot.Work.ConfigureAwait(false); }
        finally { slot.Cancel.Dispose(); }
    }

    private StreamReply Snapshot(StreamCommand command, StreamSlot slot)
    {
        var session = _currentStream == slot.Id ? _session : slot.Prepared;
        StreamPhase phase = slot.Phase;
        if (session?.DecodeFailure is not null) { phase = StreamPhase.Failed; slot.Error = "ReadFailed"; }
        else if (_currentStream == slot.Id && session is not null)
        {
            phase = session.IsDrained && !IsPlaying ? StreamPhase.Ended
                : !slot.WantsPlay ? StreamPhase.Paused : !IsPlaying ? StreamPhase.Failed
                : session.IsBuffering ? StreamPhase.Buffering : StreamPhase.Playing;
        }
        else if (session is not null && phase == StreamPhase.Buffering &&
            (session.InputEnded || session.ReadyFrames >= session.InitialBufferFrames)) phase = StreamPhase.Ready;
        slot.Phase = phase;
        return new()
        {
            Accepted = true, RequestId = command.RequestId, SessionId = slot.Id,
            Phase = phase, WantsPlay = slot.WantsPlay, CanSeek = session?.CanSeek ?? slot.Source.CanSeek,
            PositionMs = _currentStream == slot.Id ? GetTimeProgress().Item1 : session?.CurrentMs ?? 0,
            DurationMs = session is { TotalMs: > 0 } ? session.TotalMs : null,
            BufferedMs = session is null ? 0 : session.FramesToMs(session.ReadyFrames),
            SeekId = session is null ? 0 : Volatile.Read(ref session.CompletedSeekId), Error = slot.Error
        };
    }
    private static StreamReply Reject(StreamCommand command, string error) => new()
    { RequestId = command.RequestId, SessionId = command.SessionId, Error = error, Phase = StreamPhase.Failed };

    private void CancelStreams()
    {
        foreach (var slot in _streamSlots.Values) { try { slot.Cancel.Cancel(); } catch (ObjectDisposedException) { } }
        lock (_streamLock) foreach (var slot in _streamSlots.Values) RemoveStream(slot);
    }
    public async Task StopStreamingAsync()
    {
        _streamingStopping = true;
        CancelStreams();
        await Task.WhenAll(_streamWork.Keys).ConfigureAwait(false);
    }
}
