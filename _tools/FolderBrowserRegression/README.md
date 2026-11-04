# 文件夹浏览验证（2026-11-04）

- `FolderBrowserRegression`：21 项目录逻辑检查通过，直接链接生产目录身份、节点和索引；覆盖重叠扫描根、同名目录、UNC、空根、来源过滤、WebDAV 大小写和编码分隔符。
- `FolderBrowserUiRegression`：49 项检查通过，其中 37 项覆盖目录/来源页面、12 项覆盖 Composition 缓存。直接链接生产 Page/XAML、FolderViewModel、节点、索引和批量集合；使用真实 WinUI TreeView 和虚拟化列表，验证宽窄窗口左右布局、原生树选择/展开、七个相邻目录的高亮/标题/路径/歌曲一致性、快速连续选择、刷新后高亮恢复、分隔条列宽调整/最小宽度/会话比例保留、无目录返回命令、异步切换、页面重建、详情返回状态保留、失败重试和退出等待。同时链接生产 AddFolderPage/XAML，验证默认 DropDownButton 模板、来源菜单项和弹出菜单。数据库、远程来源子控件与歌曲控件为替身，保留 UI 同步上下文和延迟发布；不覆盖实际 SQLite、歌曲菜单和播放 IPC。
- `SharedStateRegression`：生产查询与投影回归通过，包括缓存失效、共享刷新屏障、最新搜索和停止后抑制发布；目录回归同时链接生产 FolderViewModel 和索引，覆盖快速 A→B→A、等待共享计算时切回、恢复后的缓存复用及 UI 上下文通知。
- 七语言静态检查：7 个文件夹独立资源键均恰好定义一次，没有属性后缀 GetString 键。改动文件保持 CRLF。
- 主项目 Debug/x64 构建通过；并行任务共用仓库，为避免 obj 竞争使用独立中间目录和输出目录。
- 旧 `ManualLocationUiRegression` 已更新页签/接口依赖并编译通过；运行时在 ready 检查之前以 0xC0000005 退出，未获得导航检查结果，未归因。未把它计入通过项。完整主程序导航、真实曲库播放与远程服务仍需集成验证。

## 复现命令

```powershell
dotnet run --project _tools/FolderBrowserRegression/FolderBrowserRegression.csproj --no-restore -p:UseSharedCompilation=false
dotnet run --project _tools/SharedStateRegression/SharedStateRegression.csproj --no-restore -p:UseSharedCompilation=false
& 'C:/Program Files/Microsoft Visual Studio/18/Professional/MSBuild/Current/Bin/MSBuild.exe' _tools/FolderBrowserUiRegression/FolderBrowserUiRegression.csproj /p:Configuration=Debug /p:UseSharedCompilation=false /nr:false
& 'C:/Program Files/Microsoft Visual Studio/18/Professional/MSBuild/Current/Bin/MSBuild.exe' WinUIMusicPlayer.csproj /p:Configuration=Debug /p:Platform=x64 /p:IntermediateOutputPath=obj/FolderBrowserCheck/ /p:OutputPath=bin/FolderBrowserCheck/ /p:UseSharedCompilation=false /nr:false
```

WinUI 测试可执行文件及 `result.txt` 位于 `_tools/FolderBrowserUiRegression/bin/Debug/net11.0-windows10.0.26100.0/win-x64/`。本轮非交互桌面的 PrintWindow 截图为黑色，未计作视觉验收证据；布局依据真实控件尺寸和位置断言。

## Composition 缓存生命周期回归

`CompositionCacheChecks.cs` 直接调用生产动效辅助类与真实 WinRT Compositor/动画，检查同一所有者复用、不同所有者隔离、CompositionObject 入口、并发发布、工厂失败重试、按压/释放/居中参数，以及活动所有者在 GC 后仍可复用缓存。

回收检查创建 20 个独立 Compositor，每个缓存一个显式反向引用所有者的托管对象和一个原生动画，随后释放所有者并只保留弱引用。诱导 GC 仅用于测试：旧静态字典保留 20/20 组所有者和缓存值，ConditionalWeakTable 实现回收全部 20 组。未向产品加入 GC 调用或主动释放仍在使用的缓存动画。

Release/x64、.NET 11.0.0-rc.2.26504.105 隔离 WinUI 回归中，预热后复用同一工厂委托调用 100000 次 GetCached，当前线程新增托管分配为 0 字节；不包含调用方创建委托、新动画或 XAML 渲染的成本，也不代表整个动效链路零分配。新增检查与原有页面检查合计 49 项通过。

## 音乐来源页资源缺失回归

用户日志 `WinUIMusicPlayer-20261104.log` 的 AddFolderPage 异常为 `Cannot find a Resource with the Name/Key DefaultDropDownButtonStyle`。修复前在独立 WinUI 测试中加载生产 AddFolderPage，复现相同异常；修复后 21 项检查通过，其中新增 3 项覆盖页面默认 DropDownButton 模板、两个来源菜单项和真实弹出菜单。修复前异常保存于 `_tools/folder-browser-addfolder-repro.log`。

导航栏返回按钮恢复为 MainPage.xaml 的常驻 Visible；不再针对文件夹页面覆盖为 Collapsed。


## 目录选择错位与拖动分栏回归

通过原生 TreeViewItem 的 UIA ISelectionItemProvider.Select 选择目录，修复前在 Artist 目录选择处复现 VM 仍停留上一次目录的问题；直接设置 TreeView.SelectedItem 无法暴露这个时序。WinUI 在 SelectionChanged 之后更新 SelectedItem，生产处理器改读事件 AddedItems。刷新时目录节点重建，布局完成后恢复当前节点的原生 IsSelected；只等待一轮布局，屏外节点在容器 Loaded 时重试，离页解除订阅。旧处理器复现日志保存于 `_tools/folder-tree-selection-repro.log`（本机忽略文件）。

分栏检查直接调用实际 Toolkit GridSplitter 的拖动处理，验证真实 Grid 的列宽变化、双向绑定、左右最小宽度以及重建页面后的比例保留。未重写分栏计算算法；真实鼠标命中与捕获测试因执行桌面无法命中测试窗口未完成，不计入通过项。
