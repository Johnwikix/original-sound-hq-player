# 音乐可选标签 WinUI 集成检查

运行 `python _tools/MusicMetadataUiRegression/prepare_fixture.py`，再构建本项目并启动生成的 exe。
窗口在屏幕外自动关闭，结果写入输出目录的 result.txt；不访问正式曲库或音乐文件。
夹具直接提取生产 MusicDetailsWindow 的可选标签区域并保留 x:Bind；标签文本取自生产资源文件。
使用实际 Music、MusicMetadataEditorViewModel 和 WinUI 控件，验证六项双向绑定、空标签默认收起、
已有标签展开、字段尺寸、草稿隔离和远程只读。此检查不代替完整属性窗口的文件写入回归。
