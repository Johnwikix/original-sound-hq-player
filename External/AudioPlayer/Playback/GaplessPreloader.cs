using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

internal enum GaplessPreparationState { Empty, Waiting, Preparing, Ready, Failed }

/// <summary>同时保留一个正在打开的操作和一个可替换请求；流锁使结果发布
/// 与引擎状态变更串行执行；发布成功前，会话及其清理归工作线程负责。</summary>
internal sealed class GaplessPreloader(object gate,
    Func<GaplessPreparation, CancellationToken, Session?> open,
    Func<GaplessPreparation, Session, bool> publish)
{
    private sealed record Work(long Generation, GaplessPreparation Preparation);
    private Work? _latest;
    private readonly Queue<Session> _retired = new();
    private CancellationTokenSource? _opening;
    private Task _worker = Task.CompletedTask;
    private bool _running, _stopped;
    private long _generation;
    // 在与引擎相同的锁内读取，不另存已接受计划的副本。
    internal GaplessRequest? Request { get; private set; }
    internal GaplessPreparationState State { get; private set; }
    internal Task Completion { get { lock (gate) return _worker; } }

    internal void SetPlan(GaplessRequest request)
    {
        lock (gate)
        {
            if (_stopped) return;
            Cancel();
            Request = request;
            State = GaplessPreparationState.Waiting;
        }
    }

    internal void Cancel()
    {
        lock (gate)
        {
            ++_generation;
            _latest = null;
            _opening?.Cancel();
            Request = null;
            State = GaplessPreparationState.Empty;
        }
    }

    internal void Start(GaplessPreparation preparation)
    {
        lock (gate)
        {
            if (_stopped || State != GaplessPreparationState.Waiting || Request != preparation.Request) return;
            State = GaplessPreparationState.Preparing;
            _latest = new(_generation, preparation);
            EnsureWorker();
        }
    }

    // 分离的会话已无渲染器借用；清理复用打开操作的工作线程，
    // 避免替换就绪计划时，在缓慢的解码器关闭操作之后无限堆积打开请求。
    internal void Retire(Session session)
    {
        lock (gate)
        {
            _retired.Enqueue(session);
            EnsureWorker();
        }
    }

    private void EnsureWorker()
    {
        if (_running) return;
        _running = true;
        _worker = Task.Run(Run);
    }

    private async Task Run()
    {
        while (true)
        {
            Work? work = null;
            CancellationTokenSource? cancellation = null;
            Session? retired;
            lock (gate)
            {
                _retired.TryDequeue(out retired);
                if (retired == null)
                {
                    if (_latest is null || _stopped)
                    {
                        _running = false;
                        return;
                    }
                    work = _latest;
                    _latest = null;
                    cancellation = new();
                    _opening = cancellation;
                }
            }
            if (retired != null) await DisposeSessionAsync(retired).ConfigureAwait(false);
            else await Prepare(work!, cancellation!).ConfigureAwait(false);
        }
    }

    private async Task Prepare(Work work, CancellationTokenSource cancellation)
    {
        Session? session = null;
        try
        {
            session = open(work.Preparation, cancellation.Token);
            lock (gate)
            {
                if (_stopped || work.Generation != _generation || cancellation.IsCancellationRequested) return;
                if (session is not null && publish(work.Preparation, session))
                {
                    session = null; // 已发布会话的所有权现归引擎。
                    State = GaplessPreparationState.Ready;
                }
                else State = GaplessPreparationState.Failed;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate)
                if (work.Generation == _generation) State = GaplessPreparationState.Failed;
            Console.WriteLine($"[gapless] preload failed: {ex.Message}");
        }
        finally
        {
            await DisposeSessionAsync(session).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(_opening, cancellation)) _opening = null;
                cancellation.Dispose();
            }
        }
    }

    private static async Task DisposeSessionAsync(Session? session)
    {
        if (session == null) return;
        try { session.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[gapless] cleanup failed: {ex.Message}"); }
        try { await session.DecodeCompletion.ConfigureAwait(false); }
        catch (Exception ex) { Console.WriteLine($"[gapless] decoder completion failed: {ex.Message}"); }
    }

    internal Task StopAsync()
    {
        lock (gate)
        {
            _stopped = true;
            Cancel();
            return _worker;
        }
    }
}

/// <summary>准备时捕获解码器配置，发布时读取 DSP 目标设置。</summary>
internal sealed record GaplessPreparation(GaplessRequest Request, Session Current, GaplessSource Source,
    int DsdRate, int DsdGain, int Latency, bool Surround, double PlaybackRate, bool BitstreamEnabled,
    SpectralEnhancementMode Enhancement = SpectralEnhancementMode.Off, int? EnhancementDeviceRate = null);
