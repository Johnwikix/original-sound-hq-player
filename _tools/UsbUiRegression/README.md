# USB 逐曲 WinUI 实机回归

连接安卓手机并选择文件传输模式；测试使用独立本地临时目录、SQLite 单表和手机 `MUSIC/OriginalSound-UsbUi-<GUID>`，完成后仅删除本次数据，不访问用户曲库。测试期间不要并发执行其他 USB 回归。

```powershell
dotnet publish _tools/UsbUiRegression -c Release -p:PublishTrimmed=true -o artifacts/usb-ui/publish
```

WinUI 的发布目录还需从对应构建目录复制 `*.xbf`（保持相对路径）及 `UsbUiRegression.pri`，然后运行 `UsbUiRegression.exe`。窗口不激活并移到屏幕外；结果写在 exe 同目录的 `result.txt`。成功必须出现 `PASS: all native USB UI regression checks completed.`，退出码为 0。

链接生产 `UsbExportCoordinator`、`UsbDeviceService`、状态更新策略、`ProgressCenter`、MTP 写入及 SQLite 台账，用真实 DispatcherQueue、ListView、InfoBar 和编译 `x:Bind` 验证：

- 每首音频成功时，在批次仍活动的 UI 线程通知中，已实现的 ListView 行立即显示新状态。
- 中间源文件缺失时，后续正常歌曲仍成功；提示准确显示音频 2/3、未发送 1、歌词失败 0 和失败标题，进度不达 100%。
- 成功覆盖不重置已有图标，记录提交成功后达到 100%。
- 可控歌词读取失败时，音频图标和台账仍成功；提示音频 1/1、未发送 0、歌词失败 1，进度不达 100%。
- 从真实 PRI 解析新增独立资源键，原生 InfoBar 的编译绑定能显示最终提示。

仅测试歌曲模型、歌词/封面来源及完整曲库状态使用隔离替身。SQLite、原生手机文件操作、协调与服务生命周期、UI 调度和绑定均为真实边界。
