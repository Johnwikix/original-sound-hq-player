# WER 崩溃诊断集成 — 2026-09-18

对应 [Windows App SDK 2.5.1 发布说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0#version-251)新增的 Windows Error Reporting 支持。目标:让未处理错误进入 WER 管道,合作伙伴中心"运行状况"报告能拿到可定位的数据(托管堆栈而不是无意义偏移)。

## 本次更改

| 文件 | 更改 |
|---|---|
| `Services/CrashReportingService.cs` | 新增。两条路径:已处理异常的 WER 主动上报(进程不退出)、致命异常的现场证据收集;另配 createdump 本地转储兜底 |
| `App.xaml.cs` | UI 线程未处理异常:记录 + `ReportHandledException` + 保留 `e.Handled = true`;后台线程异常:记录 + `OnFatalException`;日志目录提取为 `LogDirectory` 常量 |
| `Package.appxmanifest` | 包级声明 `desktop7:ErrorReporting` → `RuntimeExceptionHelperModule Path="mscordaccore.dll"` |
| `WinUIMusicPlayer.csproj` | `AppxSymbolPackageEnabled=True`;Release\|x64 `DebugType=portable`(原 `none` 连 PDB 都不生成) |

### 数据通路

**已处理异常(UI 线程,进程存活)**:`WerReportCreate(NonCritical)` 静默入队,不弹 UI。报告含参数(异常类型/消息/栈顶,WER 限制 10×260 字符)、完整异常详情临时文件、当前 Serilog 日志、当前进程 MiniDump。会话内按签名去重、总量封顶 10 份防故障风暴;提交在后台线程,不卡 UI。报告可见于本机 `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue`、事件查看器 WER 1001、可靠性监视器;用户同意诊断数据时后台上传。

**真实崩溃(后台线程未处理异常、native 崩溃,进程终止)**:清单声明的 mscordaccore 诊断模块由 WER 加载,按托管异常类型与堆栈分桶;`OnFatalException` 把当前日志 `WerRegisterFile` 进报告并 `CloseAndFlush` 刷盘;createdump 兜底在 `Documents\OriginalSoundPlayer\CrashDumps` 落 Mini 转储(自动清理 14 天前旧文件)。这条路径的数据是合作伙伴中心 Health 报告的确定来源。

**符号化**:提交商店时用 VS"打包发布"产出 `.msixupload`,自动携带 `.appxsym`(内含 portable PDB,不进 MSIX 本体)。没有符号包,合作伙伴中心只能显示偏移量。

### 验证

- Debug\|x64 构建 0 错误。
- 清单扩展经项目实际工具链(Microsoft.Windows.SDK.BuildTools 10.0.28000.2705 的 makeappx)对真实构建布局打包通过。
- WER API 签名、枚举值、参数上限均对照本机 SDK `werapi.h` 核实;`mscordaccore.dll` 导出 `OutOfProcessExceptionEventCallback/SignatureCallback/DebuggerLaunchCallback` 已实际验证(这是它能充当 WER 运行时异常辅助模块的依据)。
- 未真机验证:WER 报告生成与上传需商店签名安装环境,发布前建议手动触发一次,确认 ReportQueue 出现 `OriginalSoundHandledException` 报告且附件齐全。

## 关于 windows.diagnosticServiceModule 的实证结论

2.5.1 发布说明确认该扩展存在(自包含 .NET MSIX 应用声明诊断模块、WER 加载并收集可操作崩溃转储),但**截至今日(2026-09-18)无公开 schema**。实测三套工具链均拒绝该类别:

| makeappx | 结果 |
|---|---|
| Windows SDK 10.0.26100 | 拒绝:foundation 类别枚举不含它 |
| SDK.BuildTools 10.0.28000.2705(项目在用) | 同上 |
| SDK.BuildTools 10.0.29648.1000-preview(NuGet 最新预览) | 同上,且 schemas 目录无任何 diagnostic 字样 |

WASDK v2.5.1 源码树(git tag)中也没有该字符串,说明 schema 在 OS/SDK 侧,需后续服务更新。**现在写进清单会直接导致打包失败**,故采用文档化的 `desktop7:ErrorReporting` 指向 mscordaccore.dll——两者目的相同(声明 WER 可加载的诊断模块)。

## 未来如何切换到 windows.diagnosticServiceModule

### 前置检查(先确认工具链支持,再动手)

1. 查当时的官方 schema 文档(learn.microsoft.com 的 appxmanifest schema 一节,搜 `diagnosticServiceModule`),拿到**确切的命名空间、子元素与属性名**——下面步骤里的写法只是占位,必须以文档为准。
2. 或直接实测:用最小清单跑一次 makeappx,确认类别被接受:

   ```
   "<makeappx>" pack /o /d <测试目录> /p <输出.appx>
   ```

   测试清单在 `<Package>/<Extensions>` 放入候选声明,类别被拒会报 "…not in enumeration"。注意 `windows.errorReporting` 必须在 **Package 级** Extensions,Application 级会报错。

### 切换步骤

1. `Package.appxmanifest`:将现有 `desktop7:Extension Category="windows.errorReporting"` 整块替换为新语法的 `windows.diagnosticServiceModule` 声明,模块路径仍指向包根的 `mscordaccore.dll`(自包含 .NET 发布产物自带,导出 WER 所需回调;除非官方文档指定其他模块)。若新语法使用新命名空间,更新 `xmlns` 声明与 `IgnorableNamespaces`;若 `desktop7` 无其他使用处,一并移除。
2. 同步更新清单注释与本文档,注明切换日期与依据的 schema 版本。
3. `CrashReportingService`、csproj 符号包/PDB 配置**不需要改动**——代码层与符号层和新扩展正交。

### 切换后验证

- makeappx 打包通过(前置检查已覆盖)。
- 部署后触发一次真实崩溃(后台线程抛异常),在事件查看器 WER 1001 或 `ReportQueue` 确认报告的桶参数含托管异常信息而非裸偏移。
- 提交商店后数天,核对合作伙伴中心 Health 报告的失败分组是否按托管异常聚合。

## 已知限制

- **合作伙伴中心对 NonCritical 自定义报告无收录保证**:Health 报告文档数据源是真实崩溃。已处理异常的上报在本机 WER 与微软后端确定存在,商店面板是否展示待观察;若必须商店可见,只能放行崩溃(去掉 `e.Handled = true`,其余设施不动)。
- `desktop7:ErrorReporting` 在不认识该扩展的旧系统上被忽略(已列入 IgnorableNamespaces),应用行为不受影响,只是回到无托管分桶的普通 WER 报告。
- Release 启用裁剪,上传的 PDB 对应裁剪后二进制,个别内联/重写帧符号化可能不完整。
- 启动失败路径(OnLaunched catch)保持弹窗 + 干净退出,不走 WER,合作伙伴中心看不到这类失败(有意为之,保留用户可见 UX)。
