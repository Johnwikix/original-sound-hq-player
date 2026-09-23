# WebDAV TreeView 显示回归

运行 `./Run.ps1`。这是独立的、无需 MSIX 部署的 WinUI 应用，直接链接两个生产对话框的 XAML，打开真实 ContentDialog 并检查树节点名称、图标、延迟加入的子节点、多选和折叠后再次展开。成功后在构建输出目录生成 `browser-tree.png`、`connection-tree.png`。

`./Run.ps1 -WithoutTemplate` 会移除模板以复现旧配置，预期在节点名称检查处失败。

浏览对话框仍使用表单和事件桩，只验证模板，不覆盖播放服务。

连接对话框链接生产 XAML、代码后置、ViewModel 和 HTTP 传输，使用本地 HTTP 服务器。通过 WinUI AutomationPeer 触发真实复选框点击及 `SaveAsync` 验证：恢复唯一子目录不会扩大为父目录、父目录级联到已加载及懒加载的子目录、取消单个子目录保留其余范围、全部子目录选中不扩大扫描根、取消父目录清空子树、部分选择显示及折叠展开。

`LibraryRegression.cs` 链接生产 WebDAV 数据库方法、Music/RemoteTrack 模型及来源 ViewModel，在临时 SQLite 文件和真实 ComboBox 双向绑定上验证：缩小范围在扫描前即隐藏旧曲目、重载后仍生效、收藏和歌单 ID 保留、扩大后扫描复用 ID、移除当前来源回退全部来源并同步两处过滤器、移除其他来源保留选择。数据库文件在测试后关闭并删除。

应用主 ViewModel、播放器、凭据库、缓存及扫描服务仍为适配器，不覆盖真实 NAS 或完整应用后台扫描调度。窗口位于屏幕外，不读取用户曲库、来源或凭据。
