# WebDAV 切歌回归

`dotnet run --project _tools/PlaybackNavigationRegression --no-restore`

链接生产 `PlaybackCoordinator`、`PlaybackCommands`、`ApplicationTasks` 和生命周期代码。单线程调度队列保留异步时序；HTTP/凭据、播放后端及 UI 模型为适配器。

覆盖：在线标记过期后的连续跳过、离线标记过期后的重新探活、同源仅探测一次、上一首方向、全失败有界终止、新选曲取消旧请求、队列替换及退出取消。人为阻塞远程准备与清理 350 ms，并测量 UI 调度间隔，避免后台工作意外回到 UI 线程。

真实 WinUI 调度验证运行 `./_tools/PlaybackNavigationUiRegression/Run.ps1`（首次运行前还原该项目），共用这些场景。测试不读取用户数据库、凭据或 NAS；实际设备及真实网络体验仍需在主程序验证。

新增覆盖：真实 ListView 在探活结束前选中待播项；失败恢复、同源缓存、跨请求 15 秒重试期限、直接重试、停止待播、失败选曲后原会话仍可恢复。

`dotnet run --project _tools/RemotePlaybackRegression --no-restore` 链接生产连通性 partial、RemotePlaybackService、RemoteReadSession、缓存、桥接、StreamingClient 和协调器。测试源为本机 HTTP，播放端为隔离命名管道协议适配器，模拟缓冲尚未耗尽时仍报告 Playing；验证真实源端读错误驱动自动切本地及状态更新。数据库/凭据边界是适配器，不接触用户数据，不能替代真实解码器和设备回归。
