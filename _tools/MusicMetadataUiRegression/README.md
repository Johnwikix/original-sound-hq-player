# 音乐可选标签 WinUI 集成检查

运行 `python _tools/MusicMetadataUiRegression/prepare_fixture.py`，再构建本项目并启动生成的 exe。
窗口在屏幕外自动关闭，结果写入输出目录的 result.txt；不访问正式曲库或音乐文件。
夹具直接提取生产 MusicDetailsWindow 的等待层、编辑区可见性绑定、可选标签区域和文件信息区（含页面级样式资源），保留 x:Bind 模式；标签文本取自生产资源文件。
使用实际 Music、MusicMetadataEditorViewModel 和 WinUI 控件，验证六项双向绑定、字段常显布局与尺寸、超长路径单行不换行的只读可复制行、草稿隔离、已有标签回填和远程只读。
等待层回归从没有新增字段的旧 SQLite 库开始，链接生产迁移、ATL 标签补读和 ApplicationTasks；
确认未重扫曲库时补读完成、草稿更新、UI 线程状态通知以及等待层/编辑区切换，并覆盖保存失败和重试时的状态恢复。
隔离宿主只替换数据库初始化边界，不代替完整属性窗口、歌词加载或真实文件保存回归。
