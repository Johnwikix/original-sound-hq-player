# 应用生命周期与交互解耦

## 职责与依赖

| 类型 | 职责 | 不再承担 |
| --- | --- | --- |
| `AppInitializerService` | Host 的启动适配器 | 具体启动步骤与资源清理 |
| `StartupCoordinator` | 显式编排窗口、协议、后端、音乐库、更新日志、就绪 | 保存播放状态和执行退出 |
| `AppLifecycle` | Starting / WaitingForAgreement / Initializing / Ready / Stopping / Failed，退出取消信号 | 协议持久化、服务定位 |
| `ShutdownCoordinator` | 一次性保存、停止已完成启动的 Host、逆序清理已登记资源 | 从容器创建尚未使用过的对象 |
| `PlaybackStatePersistence` | 保留原有窗口边界、队列、音量等保存规则 | 终止进程或初始化页面 |
| `AudioProcessService` | 创建并持有本次音频子进程，退出兜底清理 | UI、许可、播放命令 |
| `TrayViewModel` | 托盘状态、窗口/导航/退出及桌面歌词命令 | 浏览页面、目录扫描、后台服务启动 |
| `PlaybackCommands` | 主界面、详情页、托盘、SMTC、快捷键、任务栏共用播放/暂停/上一首/下一首/跳转命令及可用条件 | 音频算法、设备/许可策略 |
| `LibraryWatcherService` | 显式启停文件监视、事件合并、停止与取消 | 浏览页面实例 |
| `AudioSettingsSynchronizer` | 显式订阅和解绑输出/EQ 设置同步 | 浏览列表与页面构造 |
| `AudioConversionViewModel` | 转换进度与批量转换交互，执行期间订阅进度 | 浏览页实例和系统播放事件 |

`MusicBrowseViewModel` 保留列表导航、封面/歌词展示及已有歌曲切换协调。其旧播放/转换方法是兼容转发入口；主界面与详情页 XAML 已直接绑定共享播放命令。播放后端与当前歌曲展示之间仍有适配关系：`PlaybackCommands.Previous` 和后端选歌进入既有 `PlayMusic`。本轮没有为消除这一关系重写切歌算法、跨进程协议或图片异步更新逻辑。

## 启动与退出约束

1. 托盘随主窗口创建。创建过程中不解析 `MusicBrowseViewModel`，不启动音频、目录监视或转换；显示窗口、退出始终可用。
2. 协议记录独立于生命周期。协议确认后转为 Initializing，显式启动音频进程、SMTC 初始化、设置同步与文件监视。
3. IPC、许可、音乐库汇合后推送初始音频设置。更新日志仍与协议串行，用户操作直到 Ready 才开放。
4. Host 全部启动完成后才登记 Host 停止回调并进入 Ready。`AppViewModel.IsInitialized` 是只读兼容入口，不再保存第二份状态。
5. 开始退出立即转为 Stopping 并取消启动等待，所有播放入口重新检查状态；重复退出不重复保存或释放。
6. 只有从 Ready 退出才保存播放状态、结算统计、发送停止播放。启动中退出跳过保存和停止尚未完成启动的 Host。
7. 清理操作只对已取得的实例登记。设置/事件订阅、监视器、IPC、媒体、页面和音频子进程逐项清理，某一项失败不会阻断其余项。歌词 HTTP 服务与播放详情页在实际创建时登记，退出不会为了 Dispose 而创建它们。

文件监视只有一个消费者，容量为 1 的通道合并变化通知，扫描期间的新事件进入下一轮。扫描继续使用 `LibraryOperationGate`，与初始化/手动维护互斥。停止时关闭原生监视器、取消并等待消费者完成。

目录监视开关控制实际资源生命周期：关闭时释放原生监视器、取消并等待扫描消费者；重新开启时读取当前目录并重建。设置变化与资源启停串行，应用退出后不能被设置或迟到的目录读取重新启动。

SMTC 的 Play/Pause 保留明确意图，不再都当作 toggle；共享命令防止多个入口同时发送 toggle。实际声卡和音频算法未改。

在途 toggle 期间保留最新的显式 Play/Pause 意图，后端任务在 UI 发布确认状态后完成，再判断是否需要追加操作；进入 Stopping 后丢弃待处理意图。播放列表在 UI 线程创建和发布，命令可用性通知也回到 UI 线程；已释放命令忽略迟到的队列回调。

## 验证

- `dotnet run --project _tools/LifecycleRegression --no-restore`：直接编译生产生命周期、退出协调器、播放命令、托盘 ViewModel、目录监视器；平台/音频边界替换为桩，目录事件使用真实临时文件。
- `dotnet run --project _tools/AgreementRegression --no-restore`：协议持久化和失败分支。
- `dotnet run --project _tools/LicenseRegression -c Release --no-restore`：许可与输出限制；Release 避免读取个人调试覆盖文件。
- Debug x64 打包构建通过，未关闭裁剪。
- 实际 MSIX 开发包：协议期间托盘存在、无音频子进程；拒绝与关闭退出；临时已确认夹具经过更新日志进入主界面；普通关闭后主进程与音频子进程均结束。夹具已移除，保持测试前没有协议记录的状态。

实机启动测试不代表所有设备场景已覆盖。仍需设备验收：实际 SMTC/快捷键切歌、Explorer 重启、USB 热插拔、长时间转换期间退出、Store 超时及 IPC 持续连接失败。自动命令测试不播放用户音频，也不模拟真实硬件输出。
