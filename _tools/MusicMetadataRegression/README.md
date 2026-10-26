# 音乐可选标签回归

运行：`dotnet run --project _tools/MusicMetadataRegression -c Release`

使用实际 ATL 7.18.0、SQLite、Music 模型、属性窗口草稿 ViewModel 和迁移代码。
覆盖缺失标签、取消草稿、保存期间的新输入、旧数据库、旧待写队列、迟到补读保护，以及
临时生成的 MP3、FLAC、WAV、ASF/WMA 和 AIFF 标签样本上六项直接属性的写入、重新读取、保留和清空。
排序与搜索通过 SharedStateRegression 验证真实异步列表投影；延迟写入通过 FolderScanRegression 验证。
