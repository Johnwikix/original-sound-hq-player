# 项目约定

## WinUI 本地化：避免重复出现资源键直出

- `ToolUtils.GetString` 的调用使用独立、无属性后缀的资源键，例如 `DspAutoPreamp`；在各语言 `Strings/*/Resources.resw` 中定义同名资源。
- `DspAutoPreamp.Header`、`SomeLabel.Text` 等是供 `x:Uid` 属性本地化使用的资源名，不能直接作为本项目 `GetString` 的参数。此前反复出现界面显示原始键名的问题，修改 UI 时务必检查这一点。
- 控件移除 `x:Uid`、改用代码或 `x:Bind` 取词时，同步新增或改用独立资源键，不要直接复制原属性资源名。
- 提交修改前静态检查新增的 `GetString` 调用与全部语言资源是否匹配。构建成功不能证明运行时资源解析正确。

## UI 风格

- 以用户当前布局为基准，保持 MVVM；设置状态和命令放在 ViewModel，视图负责布局和交互转发。
- SettingsCard 中并列的开关与数值输入优先同一行、垂直居中；不要给横向使用的 ToggleSwitch 添加 Header 导致卡片增高或标题重叠。
