# WebDAV TreeView 显示回归

运行 `./Run.ps1`。这是独立的、无需 MSIX 部署的 WinUI 应用，直接链接两个生产对话框的 XAML，打开真实 ContentDialog 并检查树节点名称、图标、延迟加入的子节点、多选和折叠后再次展开。成功后在构建输出目录生成 `browser-tree.png`、`connection-tree.png`。

`./Run.ps1 -WithoutTemplate` 会移除模板以复现旧配置，预期在节点名称检查处失败。

测试桩只提供表单属性和空事件入口；不验证 WebDAV 网络、播放服务及生产代码的选择同步，这些仍由其他回归或应用内测试覆盖。
