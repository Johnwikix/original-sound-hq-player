# 歌词统一回归

`dotnet run --project _tools/LyricsUnificationRegression` 编译生产解析器、仓储、文件策略、缓存、导出和来源解析代码，使用真实临时 SQLite、文件锁、文本歌词和 WAV 标签文件。提供方、全局设置和 WebDAV 外部依赖在 `Adapters.cs` 中替代；这些测试不等同真实 WebDAV 服务或动画渲染验证。

issue #27 原始附件若位于 `%TEMP%/music-player-lyrics-issue27/32505618.ttml` 和 `32552336.ttml`，会额外验证 27 行、27 译文和 0／450 词片段，否则明确输出 SKIP。原始附件未提交到仓库。

回归包含两个附件的 4 项检查，其余夹具可独立运行。`LineEndingChecks.cs` 使用完整的 65 行问题样例覆盖 CR、LF、CRLF、混合换行、逐字时间戳、偏移量和独立翻译，并经真实 sidecar 文件、JSON 缓存及 SQLite 文档验证播放展示投影。一次性缓存适配器仅替换存储位置，使用生产 JSON 持久化和版本检查，覆盖用户清空来源往返、旧 V2 缺少来源字段和迟到写入保护。测试保留临时目录便于核查备份和失败回滚。

`HighlightTimingChecks.cs` 经生产播放投影验证 QRC／KRC 长空档、TTML 重叠／同起点多行、连续短句和末行展示边界；使用真实临时文件与 SQLite 迁移记录，确认持久化原文及解析缓存不变，TTML 显式时间不变，旧格式保留展示补尾和 300ms 逐字提前量。

`PlaybackCompatibilityChecks.cs` 比较重构前播放数值，覆盖末字起点晚于行头时长、长空档、两种增强 LRC 未闭合末字、无逐字行、多字音节、短句及未知歌曲时长。`Fixtures/story-of-us.qrc.txt` 是用户提供的完整 62 行问题歌词；`story-of-us.playback.json` 是从 `44597b0a:Services/LyricsRefreshService.cs` 提取并实际编译运行原 `ParseKrcLyrics/ParseKrcWords/FixEndMs/SplitSpan` 等方法生成的冻结基线（歌曲时长 265000ms），测试不会用新实现重写期望值。逐行比较所有有效字词的文本、起点、时长和行尾；两行字面括号保留新解析器修复的正文丢失，原有字词时间仍须匹配。纯空白不参与旧正文等价断言，因为旧扫描器会丢掉部分零时长空白。

`EmptyTimestampChecks.cs` 使用用户给出的 18.515 秒重复空行样例，覆盖 CR／LF／CRLF、纯空白、QRC／KRC／增强 LRC 的连续空字标签、TTML 空片段／空行、空文件来源回退，以及有效正文中的空格、停顿和零时长字。空标签不生成歌词行，后续字保持原始时间戳；原文件不改写。
