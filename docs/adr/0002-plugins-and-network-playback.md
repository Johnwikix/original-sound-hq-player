# 插件边界与 FFmpeg 网络播放

- 日期：2026-09-20
- 状态：实现及自动化回归已完成；WinUI 实际交互和 MSIX 商店打包仍需验证。

## 目标

启动时从用户插件目录加载已启用的插件；设置提供插件管理，插件页面注册到主导航。
在线歌词和在线封面搜索独立分发。主程序保留本地歌词解析、缓存和显示。
为未来在线歌曲插件提供播放源解析与 AudioPlayer 网络播放接口，本次不接入具体音乐平台。

## 已验证的 FFmpeg 约束

仓库原有 `Libraries/FFmpeg/x64` DLL 的 `avformat_configuration()` 包含
`--disable-network --disable-everything --enable-protocol=file`；调用
`avio_enum_protocols()` 只返回 `file`。因此，只修改 C# 使其接受 HTTP URL，不能获得在线播放能力。

真实 AudioPlayer 进程的本地 HTTP 回归已复现这一限制：`avformat_open_input`
返回 `-1330794744`，网络源准备失败。该记录是旧 DLL 的基线，不代表后续网络构建的能力。

用户已授权可按任务需要从 `G:\SoftwareProject\ffmpeg` 的源码重新构建 FFmpeg、调整构建选项；
该长期约定记录在项目根目录 `AGENTS.md`。本次在线播放任务使用此能力启用网络，
不构成对所有发布包的固定协议要求。
已检查源码目录 `G:\SoftwareProject\ffmpeg\src`：版本为 `n9.0.1`，
本机存在 `G:\msys64` UCRT64 工具链。源码当前配置与应用发布 DLL 的功能列表不完全相同，
重建必须保留应用已使用的 AC3/EAC3、DSD、WavPack、PCM 与音频转换编码器。

## 决策

1. **重新构建 FFmpeg，保留原生 HTTP/HTTPS 路径。** 不采用此前讨论的
   “.NET HTTP 传输 + FFmpeg 自定义 AVIO”替代方案；该替代方案没有落地。
2. 显式启用网络及 `http`、`https`、`tcp`、`tls` 协议；Windows TLS 使用 Schannel。
   播放请求必须启用证书验证，不能因提供者传参关闭验证。
3. 在独立构建目录生成产物，保留既有源码工作目录与安装产物；验证成功后将四个运行时 DLL
   同步到 `Libraries/FFmpeg/x64`。主程序转码与独立 AudioPlayer 共用这套 DLL。
4. 运行时限制允许的协议，设置连接/读取超时、有限重连，并通过 `AVIOInterruptCB`
   取消阻塞 I/O。网络错误不得被当作正常 EOF 或触发自动下一首。
5. DLL 更新时记录源码版本、完整 configure 参数、依赖和文件校验值。
   不把一次本地 HTTP 成功扩展为已支持直播、HLS 或 DRM；未实现的能力显式返回不支持。

## 插件与播放职责

- `Plugin.Contracts`：版本化清单、数据传输模型、生命周期和能力协议，不依赖 WinUI 或数据库模型。
- `PluginHost`：每插件独立进程；加载程序集与依赖。取消或关闭不合作插件时允许终止其宿主。
- 主程序：插件启用状态、原生页面、导航、歌词结果应用和播放源解析协调。
- AudioPlayer：解码、预缓冲、Seek、DSP、设备输出与播放状态；保持 NativeAOT，不动态加载插件。
- 插件拥有当前用户权限；独立进程与程序集加载上下文不是安全沙箱。
- 歌词/封面插件独立构建，DLL、依赖和清单放在项目 `BundledPlugins/originalsound.lyrics-search`，随应用预置；主进程仍只通过独立插件宿主调用。

## 播放接口要求

源描述包含稳定资源 ID、HTTP URL、请求头、有效期、Seek 能力和缓冲策略。
签名 URL、Cookie 和鉴权头不得写入队列持久化或日志。

使用版本化、有长度限制的命名管道传递源描述，避免现有 2 KB 请求槽截断长 URL 或请求头；
实时进度继续使用 mailbox。准备、播放、暂停、Seek、停止、地址刷新都以会话 ID 关联。
准备与播放分开，首播和断流恢复使用独立阈值；最多保留当前及下一首两个源。
暂停/停止必须覆盖准备中的播放意图，迟到任务不能恢复已取消的会话。

## 安装与开发

运行 `_tools/Publish-Plugins.ps1` 独立发布歌词插件，脚本同时将 DLL、运行依赖和 JSON 清单同步到
项目 `BundledPlugins/originalsound.lyrics-search`，构建/发布复制到应用目录的同名位置。
应用启动扫描前将此整包复制到设置页显示的插件目录
（系统“文档”目录下的 `OriginalSoundPlayer/Plugins/originalsound.lyrics-search`，与 `Settings` 的父目录一致）。
目标已存在时不覆盖用户安装版本；复制使用临时目录并以重命名发布完整包。插件默认禁用，在设置中启用。
路径通过 `Environment.SpecialFolder.MyDocuments` 获取，支持系统文档目录重定向。
启用成功后主导航出现歌词搜索页。新插件默认禁用；替换文件后需要重启。
插件数据与启用列表放在同级 `PluginData`，禁用时撤销导航并停止宿主进程。

插件引用 `External/Plugin.Contracts`，实现 `IPlayerPlugin`，通过 `plugin.json`
声明 API 版本、入口程序集/类型、能力和页面。V1 页面仅支持宿主原生 `lyrics-search`
模板，尚不支持任意第三方 XAML。插件自身依赖随插件发布，宿主运行时随主应用打包。
`lyrics`/`cover` 能力用于自动匹配；`search` 用于显式搜索，选中结果由主应用保存并优先显示。
没有插件时本地、内嵌和缓存歌词仍可使用，自动在线匹配停止。

未来歌曲插件通过 `resolve` 返回 `ResolvedSource`；持久化使用 `TrackIdentity` 和
`PlaybackEntry` 的稳定 ID，临时 URL/请求头只在播放时解析。
`StreamingPlaybackService` 提供准备适配；`StreamingClient` 提供 Prepare、Play、Pause、
Seek、Stop、Status、RefreshSource。Prepare 的 Accepted 仅表示请求被接受，需检查后续
Ready/Failed；SeekId 表示实际完成的跳转。停止后会话失效；地址刷新保持资源 ID、位置和播放意图。
最多两个会话；超限明确拒绝，调用者负责释放下一首准备。凭据刷新由未来提供者重新解析后调用。
本次未接入在线歌曲平台、在线搜索/队列界面、直播、HLS 或 DRM。

## 验收与当前状态

- 已通过：真实 PluginHost 握手、请求、非合作任务取消、崩溃后重启、超长帧拒绝、关闭后禁止重启及独立歌词插件加载。
- 已通过：网络版 FFmpeg 构建、协议枚举及 DLL 部署；构建配置和 SHA-256 见 `Libraries/FFmpeg/x64/BUILD.md`。
- 已通过：真实 NativeAOT AudioPlayer 的 HTTP 预缓冲、长请求头、Range Seek、地址刷新、慢响应中暂停/停止、401、截断失败、有限重连、超时和启动中退出。
- 已通过：Windows Schannel 拒绝未受信任证书；本地 TLS 测试服务由另一个精确固定证书的客户端验证可用，未修改系统信任库。
- 已通过：真实设备播放进度推进、跳转后立即播放和暂停冻结进度；缓冲环的初始/恢复阈值与静音时进度冻结。
- 已通过：本地播放回归 281 项、歌词/封面回归 15 项、生命周期回归 7 组；所有语言新增资源键匹配且无重复。
- 已通过：主程序 Debug/Release 编译与 Release 裁剪、AudioPlayer NativeAOT 发布；现有 nullable/平台/裁剪警告仍存在。
- 未完成：WinUI 页面实际交互和明暗/高对比主题检查。de/es/ja/ru 新增词条目前使用英文。
- MSIX 商店打包未通过：本机 SDK 缺少 `mspdbcmf.exe`，打包任务另报缺少 `System.Security.Permissions 8.0.0.0`。关闭包生成的 Release 应用目录构建通过，不代表 MSIX 验收完成。

测试进程使用 `ORIGINALSOUND_IPC_SCOPE` 隔离命名对象，不连接或停止当前运行的用户播放器。
以上状态随实现更新；未完成的验证不能标记为已支持。
