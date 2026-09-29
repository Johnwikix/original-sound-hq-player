# 歌词 WinUI 集成检查

在仓库根目录运行：

```powershell
python _tools/LyricsUiRegression/prepare_fixtures.py
dotnet build _tools/LyricsUiRegression
```

启动输出目录的 `LyricsUiRegression.exe`。窗口位于屏幕外，SQLite 使用临时目录，测试不访问正式库。`result.txt` 记录编译绑定、24 种文件顺序、恢复设置、迟到请求与双编辑器冲突检查。默认完成后关闭并排空任务；使用 `--uia` 参数保留窗口 90 秒供 UIA 交互。

使用 lvt 按进程 PID dump 后，对 `OriginalInput` 设置有效 LRC，调用 `SaveDraft` 按钮；`ui-action.txt` 应为 `Saved` 和输入内容。不要复用前次 dump 的 e 编号。当前窗口只包含由生产 XAML 抽取的编辑 Pivot／优先级卡片和测试保存按钮；网络提供方与应用全局依赖使用适配器，文档、编辑 ViewModel、SQLite 仓储与 WinUI 编译绑定是生产代码。

这不是完整播放／桌面歌词或性能验收。刷新夹具脚本保留生产 x:Bind 与 UpdateSourceTrigger，仅注入测试窗口所需属性和资源文本。
