# Last.fm 接入

本播放器使用用户自备的 Last.fm 应用凭据，不内置开发者 API Key，也不要求用户把 Last.fm 密码交给播放器。

## 使用

1. 在“设置 → 常规 → Last.fm → 应用凭据”打开“申请凭据”，登录 Last.fm 并申请 API Key / Shared Secret。
2. 填入两个值，点击“保存凭据”。Secret 留空且 API Key 不变时，继续使用已保存的 Secret。
3. 点击“连接账号”，在浏览器允许访问，然后回到播放器点击“完成授权”。采用桌面 Token 授权，无需播放器协议回调。
4. 授权成功后自动启用上报。关闭开关会暂停新记录和已有队列的发送，重新开启后继续补报。
5. “解绑”清除本地 Session Key 和待补报记录，保留应用凭据。要撤销 Last.fm 服务器端权限，使用“管理 Last.fm 应用授权”。更换应用凭据也会解绑并清空队列。

## 行为与边界

- 实际播放确认后发送 Now Playing；暂停、缓冲和未确认启动不会上报为新播放。Last.fm 没有清除 Now Playing 的对应接口，暂停后展示由服务端自然过期。
- 大于 30 秒的曲目，实际收听达到一半时长或 4 分钟（取较早者）后记一次 Scrobble；Unix UTC 时间戳取本次播放开始时刻。
- 独立于本地播放统计，仅累计连续、推进中的引擎快照，并以单调时钟时间限制计入量。快进、后退、定位代次、重复快照、暂停、缓冲和无缝切歌边界不会贡献跳变的时间。
- 无缝重复同一曲目仍是新会话。WebDAV 断流后恢复原进度保留当前会话；手动重播建立新会话。
- 本地统计的门槛、入库和播放次数保持原实现。无有效歌名或歌手元数据的曲目跳过 Last.fm。
- 达标记录先原子写入队列文件，再发送。网络失败和限流采用退避重试，启动后自动恢复；按原顺序补报并保留原开始时间。Now Playing 不持久化补发。
- 队列最多 10,000 条，满时保留已有记录并提示。无法读取的队列不会被空文件覆盖；修复文件权限或内容后点击“立即重试”。
- 队列记录绑定 API Key 与账号名称。授权失效时暂停发送，为原账号保留记录；重新授权其他账号不会继承旧账号队列。
- Last.fm 返回的 accepted / ignored 会被区分；不可修复的元数据或过旧记录会被跳过并提示，失效 Session Key 则要求重新授权。
- 持久化重试不能保证服务端严格去重：若服务端已接受请求但响应在途中丢失，客户端只能保留记录等待重试。

应用凭据和 Session Key 存储于 Windows PasswordVault 的 `OriginalSoundPlayer.LastFm` 资源。非敏感偏好和待补报元数据位于“文档/OriginalSoundPlayer/Settings/LastFm.json”；该文件不含 Shared Secret 或 Session Key。

## 实现与验证

- `Services/LastFm`：签名 HTTPS API、凭据边界、原子文件队列、生命周期、会话资格与播放观察。
- `LastFmSettingsViewModel`：设置草稿、命令、忙状态与 UI 线程通知；视图只绑定现有 ViewModel。
- `PlaybackCoordinator` / `RemotePlaybackService`：新增实际播放确认通知，不改变音频引擎或 IPC 协议。
- `_tools/LastFmRegression`：生产核心代码 + 真实本机 HTTP / 文件持久化，使用虚构凭据。
- `_tools/LastFmUiRegression`：生产 XAML、ViewModel、会话观察桥及真实 WinUI 调度/绑定；音频与 Last.fm 服务端为异步边界夹具。

真实 Last.fm 账号授权、服务器接收结果与完整播放器在真实设备/WebDAV 服务器上的长时间试听仍需具备用户凭据后验证。

已执行：40 项核心检查（真实本机 HTTP / 原子文件、断网重启、账号隔离、写入失败、已接受记录清理失败、启动中退出），15 项真实 WinUI 检查（生产编译绑定、线程通知、播放观察、Light/Dark），84 项现有导航及 35 项 WebDAV 集成检查；主程序 Debug x64 构建为 0 错误，32 个取词键在七种语言中完整匹配。高对比度与真实设备的人工验收未执行。

官方参考：[桌面授权](https://www.last.fm/api/desktopauth)、[Scrobbling 规则](https://www.last.fm/api/scrobbling)、[track.scrobble](https://www.last.fm/api/show/track.scrobble)。
