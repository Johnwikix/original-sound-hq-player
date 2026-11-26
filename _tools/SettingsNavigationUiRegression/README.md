# 设置页导航回归

运行：

```powershell
./_tools/SettingsNavigationUiRegression/Run.ps1
```

工程链接生产 `View/SettingsPage.xaml(.cs)`，使用与主工程一致的 Windows App SDK 2.7.0。
真实 WinUI Window、Frame、SelectorBar、SwitchPresenter 和 DataTemplate 验证：

- 首次进入只创建常规设置；首次选中其他分区才创建对应内容，且保持满宽布局。
- 同一页面内切回分区复用视图，并再次触发 Loaded；离开分区和页面触发 Unloaded。
- 清空 BackStack 后再次进入仍复用设置页，恢复离开时的分区和标签高亮，并刷新设备。
- 前进与后退复用同一个页面并恢复离开时的分区。缓存驱逐后，新页面只创建上次离开时的分区。
- 真实 SettingsExpander 验证 General 折叠、滚动回顶和 About 默认展开在重进后正确恢复。
分区内容由记录构造及 Loaded/Unloaded 次数的替身提供；设备服务也使用替身。
因此检查覆盖宿主的真实 XAML 实例化、布局、导航与生命周期，不证明 DSP 会话、音频设备或数据库端到端正确。
字体与 PCM 等生产绑定分别由 `FontUiRegression`、`AppearanceUiRegression` 覆盖。
旧实现首次检查会发现六个分区全部构造并失败。
