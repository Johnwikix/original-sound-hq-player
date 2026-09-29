# 歌词统一回归

`dotnet run --project _tools/LyricsUnificationRegression` 编译生产解析器、仓储、文件策略、缓存、导出和来源解析代码，使用真实临时 SQLite、文件锁、文本歌词和 WAV 标签文件。提供方、全局设置和 WebDAV 外部依赖在 `Adapters.cs` 中替代；这些测试不等同真实 WebDAV 服务或动画渲染验证。

issue #27 原始附件若位于 `%TEMP%/music-player-lyrics-issue27/32505618.ttml` 和 `32552336.ttml`，会额外验证 27 行、27 译文和 0／450 词片段，否则明确输出 SKIP。原始附件未提交到仓库。

49 项包含两个附件的 4 项检查；纯合成夹具可独立运行其余检查。一次性缓存适配器仅替换存储位置，使用生产 JSON 持久化和版本检查，覆盖用户清空来源往返、旧 V2 缺少来源字段和迟到写入保护。测试保留临时目录便于核查备份和失败回滚。
