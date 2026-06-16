# 0001 — 进度定时器回调的线程护栏与零分配合并

## 状态

已采纳 / 2026-06-16

## 背景

WinUI 3 应用在自动连播切歌路径下,`AppViewModel.OnProgressTick` 在非 UI 线程被调用,触发 `RPC_E_WRONG_THREAD (0x8001010E)`。根因是 IPC 通知监听线程(`IpcService.ListenForNotificationsAsync`)直接调用 `NotificationReceived` 事件,`PlayEnded` 分支在后台线程同步 `.Wait()` 整条 `AutoPlayNextTrack` 异步链,延续到 `MusicBrowseViewModel.PlayMusic` 的 `await EnqueueAsync` 后,续接点失去 UI `SynchronizationContext`,最终落到线程池上调用 `OnProgressTick`,触发 x:Bind 跟踪代码向 WinUI DependencyProperty 写入。

应用其余进度回调路径(点击播放、IPC `PlayState` 通知)均在 UI 线程或经显式 `DispatcherQueue.TryEnqueue` marshal,仅自动连播切歌这一条路径暴露问题,日志中出现频率与"每首切一次"吻合。

## 决策

### 1. `OnProgressTick` 线程护栏 + tick 合并

缓存的 `DispatcherQueueHandler _tickMarshallerCore` 在 `AppViewModel` 构造期一次性分配(方法组转换);`OnProgressTick` 入口直接访问 `App.MainWindow.DispatcherQueue` 检测 `HasThreadAccess`,非 UI 线程时通过 `Interlocked.CompareExchange(ref _tickInflight, 1, 0)` 合并多次入队,`TryEnqueue(_tickMarshallerCore)` 后由 marshaled 回调清零 inflight 并执行核心逻辑。热路径零分配(除 `App.MainWindow` 静态属性读取外无额外开销)。

> 修正史:最初方案把 `DispatcherQueue` 缓存为字段并在构造函数里初始化,但 `AppViewModel` 在 DI 容器中先于 `MainWindow` 实例化,导致 `NullReferenceException`。后续改为懒加载属性 + 缓存字段,最终简化回直接访问 `App.MainWindow.DispatcherQueue` —— `App.MainWindow` 在整个应用生命周期内稳定,无需缓存。

### 2. `MusicBrowseViewModel.PlayMusic` 顶层 marshal

方法签名不变,顶部加 `if (!HasThreadAccess) await EnqueueAsync(() => PlayMusic(...))` 把整个方法体重定向到 UI 线程。这取代了之前"调用方必须在 UI 线程"的隐式契约。冷路径每次切歌约 3 对象(closure + delegate + `TaskCompletionSource`),可接受。

### 3. `BassPlayerCommandService.PlayEnded` 改用 cached delegate

`IpcService_NotificationReceived` 的 PlayEnded 分支从 `TryEnqueue(lambda) + AutoPlayNextTrack().Wait()` 改为 `TryEnqueue(_handlePlayEndedOnUi)`,后者在 UI 线程上设置 `IsPlaying = false` 并以 `_ = AutoPlayNextTrack()` 触发后续。消除后台线程 `.Wait()` 阻塞 IPC 监听线程的问题,同时把跨线程异步状态机搬回 UI 线程。

### 4. `MainPage.Unloaded` 调用 `StopProgressTimer`

不用 `MainWindow.Closed`,因为 `IsRunningBackend=true` 时窗口只 `Hide()` 不真正关闭,挂 `Closed` 会导致最小化到托盘时误停定时器(回归 bug)。`Unloaded` 在 MainPage 真正从 ShellFrame 移除时触发,语义正确。

`StopProgressTimer` 同时加 `Tick -= OnProgressTick` 与字段置 null,避免 WinRT `DispatcherQueueTimer` 对象引用残留。

## 取舍

- **不**为 timer Tick 引入顶层 marshal:timer 本就在 `_dispatcherQueue` 上 Tick,再加 marshal 只会引入 ~几 ms 延迟而不带来任何正确性收益。
- **不**为 `UpdatePlayBar` / `LoadLyricsToUI` / `UpdateCurrentPlayList` 做线程特化:这三个方法本身已经线程无关(内部 `Task.Run` 或自 marshal),让它们跑在 UI 线程是 PlayMusic 顶层 marshal 的副作用,可接受。
- **不**在 Release 构建加 `Debug.Assert`:当前所有调用点都已被显式约束(PlayEnded 路径 + UI 线程 click handler),暂无新增调用方的风险;若未来出现,可再加。
- **不**把 `StopProgressTimer` 入口的 `HasThreadAccess` 守卫替换为重入锁:重复调用 `StopProgressTimer` 时第二次 `_progressTimer is null` 直接 return,等价幂等。

## 影响

- `OnProgressTick` 热路径(20 Hz,72000 次/小时):0 GC 分配。
- 自动连播切歌:0 分配(PlayEnded 路径使用 cached delegate)。
- 手动切歌(冷路径):~3 对象/次(`EnqueueAsync` 内部 `TaskCompletionSource` + closure)。
- 进程退出:已存在的 `AppViewModel.Dispose` 链改为调用 `StopProgressTimer`,语义不变但释放更彻底。

## 涉及文件

- `ViewModel/AppViewModel.cs`
- `Services/BassPlayerCommandService.cs`
- `ViewModel/Pages/MusicBrowseViewModel.cs`
- `View/MainPage.xaml.cs`