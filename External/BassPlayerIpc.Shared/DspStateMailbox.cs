using System.IO.MemoryMappedFiles;

namespace BassPlayerIpc.Shared;

/// <summary>完整状态及单调递增版本；同一连接内旧快照不能覆盖新状态。</summary>
public sealed record DspStateSnapshot(long Revision, DspState State);

/// <summary>
/// DSP 专用最新状态邮箱。自动复位事件只负责唤醒，合并唤醒不会丢失最终状态。
/// 跨进程互斥保护完整载荷；只用于控制线程，禁止从音频渲染回调调用。
/// </summary>
public sealed class DspStateMailbox : IDisposable
{
    public const string Name = "AudioPlayer_DspState_v1";
    private const int Size = sizeof(long) + DspProtocol.StateSize;
    private readonly MemoryMappedFile _memory;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _changed;
    private readonly byte[] _buffer = new byte[DspProtocol.StateSize];

    public WaitHandle Changed => _changed;

    public DspStateMailbox(bool create, string name = Name)
    {
        _memory = create ? MemoryMappedFile.CreateOrOpen(name, Size) : MemoryMappedFile.OpenExisting(name);
        try
        {
            _view = _memory.CreateViewAccessor(0, Size);
            _mutex = new Mutex(false, name + "_Lock");
            _changed = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_Changed");
            if (create)
            {
                Enter();
                try { _view.Write(0, 0L); _changed.Reset(); }
                finally { _mutex.ReleaseMutex(); }
            }
        }
        catch
        {
            _changed?.Dispose();
            _mutex?.Dispose();
            _view?.Dispose();
            _memory.Dispose();
            throw;
        }
    }

    private void Enter()
    {
        try
        {
            if (!_mutex.WaitOne(1000)) throw new TimeoutException("DSP snapshot lock timed out.");
        }
        catch (AbandonedMutexException)
        {
            // The former owner may have died halfway through a write. Never expose that payload.
            _mutex.ReleaseMutex();
            throw new InvalidOperationException("DSP snapshot owner exited during access.");
        }
    }

    public DspStateSnapshot? Read()
    {
        Enter();
        try { return ReadLocked(); }
        finally { _mutex.ReleaseMutex(); }
    }

    private DspStateSnapshot? ReadLocked()
    {
        long revision = _view.ReadInt64(0);
        if (revision <= 0) return null;
        _view.ReadArray(sizeof(long), _buffer, 0, _buffer.Length);
        return new(revision, DspProtocol.ReadState(_buffer));
    }

    public bool Publish(DspState state)
    {
        Enter();
        try
        {
            var previous = ReadLocked();
            if (previous?.State == state) return false;
            long revision = checked((previous?.Revision ?? 0) + 1);
            DspProtocol.WriteState(_buffer, state);
            _view.Write(0, 0L); // Invalidate before modifying bytes, including on writer failure.
            _view.WriteArray(sizeof(long), _buffer, 0, _buffer.Length);
            _view.Write(0, revision);
            _changed.Set();
            return true;
        }
        finally { _mutex.ReleaseMutex(); }
    }

    public void Dispose()
    {
        _changed.Dispose();
        _mutex.Dispose();
        _view.Dispose();
        _memory.Dispose();
    }
}
