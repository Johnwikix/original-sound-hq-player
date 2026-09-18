# WER 崩溃诊断集成 — 2026-09-18

对应 [Windows App SDK 2.5.1 发布说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0#version-251)新增的 Windows Error Reporting 支持。目标:让未处理错误进入 WER 管道,合作伙伴中心"运行状况"报告能拿到可定位的数据(托管堆栈而不是无意义偏移)。

## 决策与当前状态(2026-09-18)

**保留——代码级 WER 诊断,全部生效**:已处理异常的 NonCritical 报告(托管类型/消息/栈顶作为报告参数,附件含完整堆栈、日志、进程转储)、真实崩溃的日志附件与刷盘、createdump 本地转储、符号包。真实崩溃虽无托管分桶,但堆栈仍可从 WER 报告(符号化后)与本地转储还原——有总比没有好,故保留。

**放弃——清单级 WER 运行时异常辅助模块注册**。`desktop7:ErrorReporting` 声明部署失败
(0x80073CF6:"扩展要求将 CompatMode 属性设置为 classic,以注册运行时异常模块")。补上
`desktop7:CompatMode="classic" desktop7:Scope="user"` 与
`classicAppCompat_8wekyb3d8bbwe` 能力可以过 schema 校验(makeappx 要求能力名首字母小写),
但该能力属于**自定义能力**,需微软审批签发 .sccd 文件,第三方应用实际拿不到——已有开发者
补齐全部属性后仍部署失败于自定义能力校验(0x80073CF9),issue 开放两年未解决
([MicrosoftDocs/winrt-related#394](https://github.com/MicrosoftDocs/winrt-related/issues/394))。
`WerRegisterRuntimeExceptionModule` API 也绕不开:文档明确要求 DLL 已列入注册表的 WER
异常模块列表,MSIX 的注册表虚拟化写不进 WER 服务读取的位置。清单/注册表级的辅助模块
注册对第三方 MSIX 是死路,推测这正是 WASDK 2.5.1 发明 `windows.diagnosticServiceModule`
的动机。**待其正式上线后按下文指南切换,补回真实崩溃的托管分桶。**

## 本次更改

| 文件 | 更改 |
|---|---|
| `Services/CrashReportingService.cs` | 新增。两条路径:已处理异常的 WER 主动上报(进程不退出)、致命异常的现场证据收集;另配 createdump 本地转储兜底 |
| `App.xaml.cs` | UI 线程未处理异常:记录 + `ReportHandledException` + 保留 `e.Handled = true`;后台线程异常:记录 + `OnFatalException`;日志目录提取为 `LogDirectory` 常量 |
| `Package.appxmanifest` | 无扩展(当日撤销 desktop7:ErrorReporting 声明,原因见顶部;注释留档) |
| `WinUIMusicPlayer.csproj` | `AppxSymbolPackageEnabled=True`;Release\|x64 `DebugType=portable`(原 `none` 连 PDB 都不生成) |

### 数据通路

**已处理异常(UI 线程,进程存活)**:`WerReportCreate(NonCritical)` 静默入队,不弹 UI。报告含参数(异常类型/消息/栈顶,WER 限制 10×260 字符)、完整异常详情临时文件、当前 Serilog 日志、当前进程 MiniDump。会话内按签名去重、总量封顶 10 份防故障风暴;提交在后台线程,不卡 UI。报告可见于本机 `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue`、事件查看器 WER 1001、可靠性监视器;用户同意诊断数据时后台上传。**这是当前唯一能把托管堆栈送进 WER 报告参数的途径**(清单级辅助模块注册不可用,见顶部说明)。

**真实崩溃(后台线程未处理异常、native 崩溃,进程终止)**:WER 生成原生崩溃报告(分桶只有原生偏移);`OnFatalException` 把当前日志 `WerRegisterFile` 进报告并 `CloseAndFlush` 刷盘;createdump 兜底在 `Documents\OriginalSoundPlayer\CrashDumps` 落 Mini 转储(自动清理 14 天前旧文件)。这条路径的数据是合作伙伴中心 Health 报告的确定来源;托管堆栈还原靠转储 + 符号包,而不是 WER 分桶。

**符号化**:提交商店时用 VS"打包发布"产出 `.msixupload`,自动携带 `.appxsym`(内含 portable PDB,不进 MSIX 本体)。没有符号包,合作伙伴中心只能显示偏移量。

### 验证

- Debug\|x64 构建 0 错误;撤销清单扩展后重新构建、真实布局 makeappx 打包通过。
- WER API 签名、枚举值、参数上限均对照本机 SDK `werapi.h` 核实;`mscordaccore.dll` 导出 `OutOfProcessExceptionEventCallback/SignatureCallback/DebuggerLaunchCallback` 已实际验证(具备充当 WER 运行时异常辅助模块的条件,只被能力墙挡住)。
- 部署实测:`desktop7:ErrorReporting` 无属性 → 0x80073CF6 要求 CompatMode=classic(用户环境,Win11 26200)。
- 未真机验证:WER 报告生成与上传需商店签名安装环境,发布前建议手动触发一次,确认 ReportQueue 出现 `OriginalSoundHandledException` 报告且附件齐全。

## 关于 windows.diagnosticServiceModule 的实证结论

2.5.1 发布说明确认该扩展存在(自包含 .NET MSIX 应用声明诊断模块、WER 加载并收集可操作崩溃转储),但**截至今日(2026-09-18)无公开 schema**。实测三套工具链均拒绝该类别:

| makeappx | 结果 |
|---|---|
| Windows SDK 10.0.26100 | 拒绝:foundation 类别枚举不含它 |
| SDK.BuildTools 10.0.28000.2705(项目在用) | 同上 |
| SDK.BuildTools 10.0.29648.1000-preview(NuGet 最新预览) | 同上,且 schemas 目录无任何 diagnostic 字样 |

WASDK v2.5.1 源码树(git tag)中也没有该字符串,说明 schema 在 OS/SDK 侧,需后续服务更新。结合 desktop7:ErrorReporting 的自定义能力墙,该扩展大概率就是微软为自包含 .NET MSIX 提供的、**免 classic 注册/免自定义能力**的正规通道。

### 上线判定(切换前置)

满足以下任一条即可认为正式上线;动手前仍按"前置检查"用最小清单实测打包:

1. learn.microsoft.com 的 appxmanifest schema 出现 `diagnosticServiceModule` 的元素文档,拿到确切的命名空间、子元素与属性名;
2. 项目所用 `Microsoft.Windows.SDK.BuildTools` 的 makeappx 接受该类别(测试命令见下文"前置检查")。

例行关注渠道:WindowsAppSDK release notes、`Microsoft.Windows.SDK.BuildTools` NuGet 版本更新(出现比 10.0.29648 更新的正式版时重测一次)。

## 未来如何切换到 windows.diagnosticServiceModule

### 前置检查(先确认工具链支持,再动手)

1. 查当时的官方 schema 文档(learn.microsoft.com 的 appxmanifest schema 一节,搜 `diagnosticServiceModule`),拿到**确切的命名空间、子元素与属性名**——下面步骤里的写法只是占位,必须以文档为准。
2. 或直接实测:用最小清单跑一次 makeappx,确认类别被接受:

   ```
   "<makeappx>" pack /o /d <测试目录> /p <输出.appx>
   ```

   测试清单在 `<Package>/<Extensions>` 放入候选声明,类别被拒会报 "…not in enumeration"。

### 切换步骤

1. `Package.appxmanifest`:在 `<Package>/<Extensions>`(注意是 **Package 级**,Application 级会报错)加入新语法的 `windows.diagnosticServiceModule` 声明,模块路径指向包根的 `mscordaccore.dll`(自包含 .NET 发布产物自带,已验证导出 WER 所需回调;除非官方文档指定其他模块)。若官方语法使用新命名空间,更新 `xmlns` 声明与 `IgnorableNamespaces`。**不要**给新扩展添加 CompatMode=classic(除非文档明确要求)——classic 注册方式已被实证是第三方应用的死路。
2. 部署验证:重新打包安装,确认不再出现 0x80073CF6/0x80073CF9 类部署错误。
3. 同步更新清单注释与本文档,注明切换日期与依据的 schema 版本。
4. `CrashReportingService`、csproj 符号包/PDB 配置**不需要改动**——代码层与符号层和新扩展正交。

### 切换后验证

- makeappx 打包通过(前置检查已覆盖)。
- 部署后触发一次真实崩溃(后台线程抛异常),在事件查看器 WER 1001 或 `ReportQueue` 确认报告的桶参数含托管异常信息而非裸偏移。
- 提交商店后数天,核对合作伙伴中心 Health 报告的失败分组是否按托管异常聚合。

## 已知限制

- **合作伙伴中心对 NonCritical 自定义报告无收录保证**:Health 报告文档数据源是真实崩溃。已处理异常的上报在本机 WER 与微软后端确定存在,商店面板是否展示待观察;若必须商店可见,只能放行崩溃(去掉 `e.Handled = true`,其余设施不动)。
- **真实崩溃的 WER 分桶无托管堆栈**(辅助模块注册被能力墙挡住,见顶部):分桶按原生偏移聚合,同一托管异常在不同内联布局下可能分裂成多个桶;完整堆栈要看报告附件(日志/转储)或合作伙伴中心符号化后的堆栈。
- Release 启用裁剪,上传的 PDB 对应裁剪后二进制,个别内联/重写帧符号化可能不完整。
- 启动失败路径(OnLaunched catch)保持弹窗 + 干净退出,不走 WER,合作伙伴中心看不到这类失败(有意为之,保留用户可见 UX)。
