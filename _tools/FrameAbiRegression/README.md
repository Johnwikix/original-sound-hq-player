# 帧事件原始 ABI 回归

运行 `powershell -File _tools/FrameAbiRegression/Run.ps1`。使用屏幕外隔离 WinUI 窗口，不启动音频引擎、不访问音乐库或网络；结果写入输出目录 `result.txt`。

- 真实 `CompositionTarget.Rendering`、Win2D Update 与托管事件并行对照 elapsed ticks、游戏循环线程、暂停/恢复、解绑/重订阅。
- 保持真实 Win2D Update 未返回，在 UI 线程调用 Dispose，检查返回前在途回调已经结束。
- 真实合成宿主和独立高级歌词控件的加载、卸载、重载及最终关闭；现有 LyricsRenderRegression 另验证生产桌面歌词和歌词原生资源。
- 可控 COM 事件源通过真实原生 Invoke 调用生产生成的委托，覆盖注册失败、异常 HRESULT、空参数、64 位 timing 布局、重复解绑、自行解绑、跨线程解绑与回调内解绑并发。
- 模拟原生 remove 失败后仍投递事件，检查迟到回调不读取已退休的参数；实验性强制 GC 检查回调目标不再被原生委托保留。
- 在同一线程预热 1000 次后执行 10000 次原始 Invoke，检查 Rendering 和 Update 桥接本身的托管分配；Update 重用一个真实 Win2D 原生 event args。此结果不代表整个绘制链零分配，也不是自然 GC/STW 基准。

反射仅用于连接生产订阅的私有注册入口与受控 COM 源，不替换回调、COM 生命周期或并发屏障。真实 WinUI/Win2D 验证直接使用公开订阅入口。运行时为项目现有 CsWinRT 包，本回归不修改 pressure 补丁。
