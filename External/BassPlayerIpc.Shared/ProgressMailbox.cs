using System.IO.MemoryMappedFiles;

namespace BassPlayerIpc.Shared;

public readonly record struct ProgressSnapshot(long Revision, long Epoch, long CurrentMs, long TotalMs, long Timestamp, bool Playing, long SeekId = 0);

/// <summary>Single publisher, latest-only telemetry; no request queue or cross-process wait.</summary>
public sealed unsafe class ProgressMailbox : IDisposable
{
    public const string Name = "AudioPlayer_Progress_v1";
    private readonly object _gate = new();
    private readonly MemoryMappedFile _memory;
    private readonly MemoryMappedViewAccessor _view;
    private readonly bool _writer;
    private byte* _pointer;
    private long* _data;
    private long _version;

    public ProgressMailbox(bool create, string name = Name)
    {
        _writer = create;
        _memory = create ? MemoryMappedFile.CreateOrOpen(name, 56) : MemoryMappedFile.OpenExisting(name);
        try
        {
            _view = _memory.CreateViewAccessor(0, 56);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _data = (long*)(_pointer + _view.PointerOffset);
            if (create) Interlocked.Exchange(ref _data[0], 0);
        }
        catch { _view?.Dispose(); _memory.Dispose(); throw; }
    }

    public void Publish(ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_writer) throw new InvalidOperationException("Only the publisher may write progress.");
            if (_data == null) return;
            long version = _version += 2;
            Interlocked.Exchange(ref _data[0], version - 1);
            Volatile.Write(ref _data[1], snapshot.Epoch);
            Volatile.Write(ref _data[2], snapshot.CurrentMs);
            Volatile.Write(ref _data[3], snapshot.TotalMs);
            Volatile.Write(ref _data[4], snapshot.Timestamp);
            Volatile.Write(ref _data[5], snapshot.Playing ? 1 : 0);
            Volatile.Write(ref _data[6], snapshot.SeekId);
            Interlocked.Exchange(ref _data[0], version);
        }
    }

    public bool TryRead(out ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            snapshot = default;
            if (_data == null) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long version = Volatile.Read(ref _data[0]);
                if (version == 0 || (version & 1) != 0) continue;
                var value = new ProgressSnapshot(version, Volatile.Read(ref _data[1]), Volatile.Read(ref _data[2]),
                    Volatile.Read(ref _data[3]), Volatile.Read(ref _data[4]), Volatile.Read(ref _data[5]) != 0, Volatile.Read(ref _data[6]));
                Thread.MemoryBarrier();
                if (version != Volatile.Read(ref _data[0])) continue;
                snapshot = value;
                return true;
            }
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_data == null) return;
            _data = null;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
            _view.Dispose();
            _memory.Dispose();
        }
    }
}
