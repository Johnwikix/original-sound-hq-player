# 本地 CUE 整轨与目录封面适配方案

设计与实施记录 · 2026-11-22

目标是让包含一份外置 CUE、一份整轨音频和目录封面的专辑，进入现有曲库后能逐曲浏览、收藏、选版、播放和导出。原盘镜像保存在原位置。建议首期覆盖本地、可精确定位的 PCM 解码路径，以 FLAC、WAV 为发布验收格式。目录封面同时惠及普通本地单曲。WebDAV、设备端 MTP 曲库、内嵌 CUESHEET、DSD/Atmos 位流切片各有独立的资源和时间语义，另列扩展范围。

## 1. 真实专辑与验收基准

已只读检查 `F:\Danny\music\[2011.11.19] Sweet Ices Cream`。目录有 `Sweet Ices Cream.cue`、`Sweet Ices Cream.flac`、`cover.jpg`、`back.jpg` 和一份 auCDtect 报告。CUE 是无 BOM 的有效 UTF-8，`FILE "Sweet Ices Cream.flac" WAVE` 引用实际 FLAC。文件头只有 STREAMINFO、VORBIS_COMMENT 和 PADDING，既无内嵌曲目表也无图片块。ffprobe 读到 44,100 Hz、16 bit、双声道和 94,518,159 个源采样帧。正面图是 600 × 600 的 JPEG。

预期普通曲库显示 Kano 的专辑《Sweet Ices Cream》，年份 2011，流派保留 `J-Pop / Utaite / Doujin`，共 9 首。每轨均可用作独立的队列出现项。下表起点按声道同步的源采样帧计数，结尾取下一行起点，末轨结尾取 94,518,159。界面显示可取整，解码和测试保留整数边界。CD 时间码一秒为 75 帧，本例每帧对应 588 个源采样帧。[RFC 9639 `8.7.1](https://www.rfc-editor.org/rfc/rfc9639.html#section-8.7.1)

| 轨号 | 曲名 | CUE INDEX 01 | 源采样起点 | 时长（秒，取三位） |
|---|---|---|---:|---:|
| 01 | 小夜子 | 00:00:00 | 0 | 253.653 |
| 02 | Human -Bambi ver- | 04:13:49 | 11,186,112 | 222.453 |
| 03 | 初恋リグレット | 07:56:08 | 20,996,304 | 189.760 |
| 04 | 6900000000 | 11:05:65 | 29,364,720 | 243.093 |
| 05 | マーメイド | 15:08:72 | 40,085,136 | 202.293 |
| 06 | Mad Lovers | 18:31:19 | 49,006,272 | 277.493 |
| 07 | Dear | 23:08:56 | 61,243,728 | 245.600 |
| 08 | intention | 27:14:26 | 72,074,688 | 280.480 |
| 09 | マッシュルームマザー | 31:54:62 | 84,443,856 | 228.442 |

用工程已引用的 `z440.atl.core 7.20.1` 的 `CatalogDataReaderFactory` 读取本例，得到正确的专辑、艺术家、九个曲名及曲长。返回的九个 `Track.Path` 均为空，`Year` 为 0，`Genre` 为空，两个 REM 字段进入目录级 Comments。建议复用 ATL 和现有探测链读取物理音频；CUE 边界由小型只读解析器直接保留 FILE、TRACK、INDEX 原值。这样可以测试多文件、编码和损坏输入，也避免通过累加浮点时长反推起点。[ATL 官方读取示例](https://github.com/Zeugma440/atldotnet/wiki/3.-Usage-_-Code-snippets#reading-cue-sheet-contents)

## 2. 现有链路及改动位置

| 链路 | 已核实的行为 | 改动位置 |
|---|---|---|
| 扫描与监视 | `AddFolderService.EnumerateMusicPaths` 只接受音频扩展名，`ReadMusicAsync` 只返回一条。新目录按已存在路径跳过。增量扫描建立 `Dictionary<string, Music>`，提交时 `WHERE Path=? ... LIMIT 1`。音频原地不变时修改 CUE 或图片不会触发相应更新。 | `Services/AddFolderService.cs`、`InitialFileScan.cs`、`MusicDatabaseService.Scanning.cs`、`LibraryWatcherService.cs`、`AutoRescanService.cs` |
| 加载、浏览与选版 | `Music.Id` 是版本，`SongId` 是歌曲，歌单另有出现项 ID。`LibraryProjectionService` 的普通列表按 Song 折叠，文件夹按版本展示。`SongVersionStore.Automatic` 以规范化标题、专辑和艺术家署名集合归并。 | `Model/Music.cs`、`Services/LibraryQueries.cs`、`LibraryProjectionService.cs`、`FolderBrowserIndex.cs`、`SongVersionStore*.cs`、`SongVersionPolicy.cs` |
| 播放器 | 手动播放发 `PlayOccurrence`，初始化发 `SetMusicUrl`，预载和手动过渡用 `GaplessRequest`。现有请求以路径定位。`Session.Open` 以解码器整轨时长创建会话并立即起解码线程。 | `BassPlayerCommandService`、`IpcService`、`PlaybackCoordinator.Gapless`、`External/BassPlayerIpc.Shared`、`AudioPlayer/Playback/PlaybackEngine*`、`Session`、`Decode/PcmDecoder` |
| 封面 | `ToolUtils.GetRawImage` 的本地路径依次涉及版本覆盖、原图缓存、内嵌图和网络取图。`CoverLoadQueue` 有独立缩略图命中流程，`CoverPresentationService` 也可直接打开 raw 或缩略图。当前新鲜度检查使用音频时间。WebDAV 已有单独的 cover.jpg/folder.jpg 回退。 | `Utils/ToolUtils.cs`、`Reader/AudioCoverReader.cs`、`Utils/CoverLoadQueue.cs`、`Services/CoverPresentationService.cs`、`Helper/PlaybackCoverCache.cs` |
| 文件操作 | 歌曲列表、收藏及分组详情的单选和多选删除直接把 `Music.Path` 交给 `DeleteFileFromDisk`。单曲待写标签以路径为主键，专辑写标签逐首打开文件；转码与 USB 原样复制也取该路径。 | `SongListViewModel`、`FavouritePlayListViewModel`、`MusicGroupDetailViewModel`、`MusicDatabaseService.Metadata/Album`、`AudioConverterService`、`UsbWriterHelper` |

## 3. 身份与曲库升级

建议继续使用 `Music` 表保存可播放版本。每个 CUE 子轨有自己的 `Music.Id`，首次入库分配独立的 `SongId`，`Music.Path` 继续存真实音频路径，`Extension` 和 `CodecId` 取自音频。`DiskNumber`、`TrackNumber`、`Duration` 保持现有展示含义。给解码、转码、分析共用一个不可变的输入描述，携带真实路径、源采样率和可选的半开区间 `[StartSample, EndSample)`。整文件用空值表示全长；有值的区间必须满足 0 ≤ start < end ≤ 源总采样数。真实路径仍可交给资源管理器、音频探测和文件存在性检查。

| 数据 | 建议持久化 | 约束及用途 |
|---|---|---|
| CUE 清单 | `CueSheet` 保存稳定 ID、规范化路径、最近成功解析的内容指纹；记录每个引用的音频路径、文件大小、UTC 修改时间及采样信息 | 唯一约束限定本地清单；音频路径建反查索引，支持依赖改变时找到整组 |
| 曲目版本 | `Music` 增加可空的 `CueSheetId`、原始 `CueTrackNo`、`StartSample`、`EndSample`。源采样率使用经验证的音源值 | CUE 行的有效键是 `(CueSheetId, CueTrackNo)`。标题、用户可编辑的展示曲号和起止点均可变。路径索引允许多行，逐轨更新按 ID 或清单轨键 |
| 旧整轨 | 原有整轨行增加独立的“已被清单展开”标识 | 从默认浏览、扫描计数、自动归并和推荐候选滤除；历史、显式旧歌单、固定版本和既有收藏仍可按原 ID 找到整轨。已有普通歌单按原 Song 解析时也须有兼容回退 |
| 曲目编辑与封面 | 对 CUE 版本持久化用户改过的元数据字段及其覆盖值；沿用 `CoverOverridePath` 保存每版本用户封面。另记自动封面来源及修订 | 重扫先计算源标签，再叠加显式覆盖（包括清空）。图片来源变化与曲目元数据修订可独立发布 |

`LibraryDatabaseMigration.CurrentVersion` 当前为 4。建议本变更升到 5，统一在 `LibraryDatabaseMigration.OpenAsync` 的回调中幂等建表、补列、建索引，全部成功并检查完整性后提交 `main.user_version`。普通和远程旧行默认整文件，原 ID 和关联保留。升级数据库时无需挂载音乐盘；在升级后首次成功扫描该目录时才展开 CUE。若调整 `SongAutoLink` 触发器或持久化分桶语义，另行升级 `SongVersionSchemaRecord` 的版本，以便重建受影响的索引和待办。

展开时在一次事务里保存清单、协调新旧成员、创建每轨 Song、更新旧整轨可见性；沿用提交后的自动归并和 UI 刷新流程。旧整轨的播放次数、历史、手动首选、歌单位置、独立歌词、封面覆盖都归旧 ID，无法可靠归属到其中一轨。旧整轨有待写文件任务时，先按原任务的 ID、文件指纹及修订处理，再重读源并展开，禁止重定向给某个子轨。确认移除 CUE 且音源仍在时恢复旧整轨，首次入库就有 CUE 的情况则按需创建整轨。子轨标记缺失并保留引用，清单恢复后按原 ID 激活。`IsMissing`、`IsDetached` 和“已展开”表达三种不同事实。

## 4. 扫描、增量与监听

1. **以目录为发现单元。** 每个目录枚举一次，收集音频、CUE 和已知封面候选名，先解析 CUE、确定被其接管的音频，再为剩余音频创建普通扫描任务。大型目录树按目录推进。启动扫描已有路径快照、首次添加、手动重扫、仅变更扫描、监听对账和外部导入共享同一套展开逻辑。沿用 `ScanPipeline` 的有界队列与取消；一个清单作为不可拆的提交单元。进度按已检查输入，文件夹歌曲数按可浏览轨数。
2. **关联文件。** 按 CUE 所在目录解析带空格、日文、方括号的字面路径，使用实际音频格式探测；`FILE` 行末的 `WAVE` 也可对应样例 FLAC。首期限定同目录引用。可支持多 `FILE`，每个源单独计算起止点和 EOF。一个源只做一次音频探测。整张清单都为一文件一轨且覆盖全文件时，优先装饰或复用现有单文件版本，沿用旧 ID。若文件名不符，建议首期报告缺失，保留现有可播放项。两份不同 CUE 对同一音源提出冲突分轨时保留最近一次验证通过的映射；首次发现则保留整轨并给出文件级诊断。相同映射可去重。
3. **解析规则。** 识别文件级与轨级 `TITLE/PERFORMER`、`REM DATE/GENRE/DISCNUMBER`、`TRACK … AUDIO`、`INDEX 00/01`。子轨标题取轨级，艺术家可回退到全盘 PERFORMER；专辑、专辑艺术家取清单全局，物理标签补缺，缺标题时生成带轨号的占位并排除自动归组。编码先识别 BOM，再尝试严格 UTF-8；历史日文、中文编码使用可检测且可诊断的回退。限制清单大小和行数，验证引用、轨号唯一、秒/帧范围、单源索引单调、区间非空且不超过实际 EOF。混合数据轨、缺少 INDEX 01、无法精确取得源长度等不完整映射走失败分支。
4. **时间规则。** 将 `MM:SS:FF` 转为整数 CD 帧。对同一源用同一个整数换算规则得到绝对采样边界，先换算边界再相减。建议“点歌从 INDEX 01 起，相邻顺播保留下一首 INDEX 01 前的实际间隙”，首轨 INDEX 01 前的隐藏音频不单列。`PREGAP/POSTGAP` 合成静音首期不额外生成；这一策略需列入格式测试。末轨以本 FILE 的源采样总数收尾，避免用四舍五入的整盘毫秒折算。
5. **重扫与恢复。** CUE 更新按持久化轨键协调，只改名或微调边界沿用 ID；若删轨、插轨重编号或改指向，先用原音源、唯一索引位置及曲目标签核对旧成员，发现冲突时保留旧缺失行、创建新身份。用独立的映射有效位和局部唯一索引约束当前映射；退役行保留原轨号和 MusicId。重编号在同一事务先撤下旧映射再登记新映射，避免同号新轨覆盖历史或交换轨号时撞索引。监听清单和音源重命名应在现有 watcher 合并为文件路径集合之前保留旧新配对，证据唯一时保留清单 ID。拒绝用标题单独恢复身份。
6. **事件与失败。** 监听 CUE、音频和候选图片的创建、替换、删除、改名；文件已被删时仍能按父目录或依赖索引对账。图片变化只刷新封面。目录不可达、复制到一半、文件锁、解析错误和取消保留上个成功快照并允许重试。首次遇到坏 CUE 时仍可导入完整音频。只有成功解析并提交的新清单或已确认删除才撤销旧映射。继续使用 `LibraryOperationGate` 协调手动操作和 watcher；后台 I/O 完成后复核输入修订，提交后才在 UI 线程发布。

## 5. 版本、展示与外部打开

- **归并规则。** 每轨先获得独立 Song，允许与另一份单曲 FLAC/MP3/远程版本按现有标题、专辑、署名规则关联，沿用手动首选、来源优先级、质量排序及精确版本播放。扫描器的身份匹配和 Song 自动归组独立处理。同一清单的不同轨位即使同名同艺术家也应保持独立。把该约束放进 `SongVersionStore.Automatic` 整个候选桶的合并决策，并处理经第三个单曲间接合并的情形。候选归属不唯一时保留待人工选择；手动拆分、排除、合并与撤销照常持久化。
- **展示与查找。** 文件夹列出 9 轨；全部歌曲、专辑、艺术家、搜索、收藏、歌单、首页沿用各页现有投影。专辑与“播放整张专辑”统一以碟号、源轨序、稳定 ID 排序；部分 `AlbumViewModel` 入口目前只按 TrackNumber 排序。详情和版本弹窗增加“CUE 第 02 轨 · 起止时间 · 源文件”，扩展名仍显示 FLAC。“打开位置”定位真实音频。修改 `Music.MatchesLibraryRow`、艺术家封面代表项、详情编辑草稿与歌词/频谱快照的字段复制，避免刷新后退回整轨语义。
- **队列与统计。** `EntryId` 区分重复出现项，`RequestedMusicId` 区分固定版本，`MusicId/SongIdAtPlay` 保留历史语义。当前曲目、高亮、恢复播放、Last.fm 上报与歌词均按所选子轨。`CurrentTrackLookup`、`State/PlaybackQueueState.cs` 和 `SpectrogramViewModel` 对 `Id=0` 的回退改比临时清单轨键。`LibraryPlaybackReconciler` 目前刻意保留在播对象；建议元数据/封面可更新，已打开会话冻结区间并在下一次播放采纳新版。映射修订应撤销旧预载。
- **外部打开。** 拆开“音频类型”“可打开的清单”“扫描依赖”三个判定。`ActivationFileResolver`、MSIX 文件关联和已运行实例的 `App.HandleActivation` 均需认可清单。单实例协议继续转发原路径，由接收端的 `OneShotPlaybackService` 展开有序曲目集合。建议双击 CUE 从首轨起播并按清单入队；双击其引用音频保持明确的整轨播放，可用已有整轨 ID 或一次性整轨，避免重新暴露重复库项。固定盘导入保留项目现有策略，可移动盘的一次性曲目使用清单轨键。`FindMusicByPathAsync` 目前 `LIMIT 1`，应提供按普通整轨、按清单轨键、按物理路径枚举全部引用三种明确查询。外部导入存在性对账也要核对清单成员，单凭 FLAC 存在不能恢复所有子轨。

## 6. 播放、无缝切轨与进度

`PcmDecoder.SeekToMs` 调用 `av_seek_frame(..., AVSEEK_FLAG_BACKWARD)` 并清空解码器与重采样器，随后读取解码帧。单纯“打开整盘后 seek 到开始毫秒，再让 UI 计时切歌”会在缓冲、暂停、倍速或 seek 时越界。应当由播放器保证子轨的第一个和最后一个采样。

1. 在共享 IPC 中为显式播放、启动准备、QueueNext、TransitionNow 统一编码路径及可选采样区间，保留当前 playbackId、epoch、token、长度和边界验证。新旧协议通过新命令或明确版本判定。播放器处理不支持的区间请求时报告失败，防止误播整盘。打开源时验证采样率及区间仍落在实际音频内；失败回到现有版本选取流程，精确版本请求保留其失败语义。
2. 在 `PlaybackEngine` 同时保存请求中与活动中的完整源描述，覆盖淡出期间替换请求、暂停后预备、切设备、重建输出、失败恢复和无缝交接。`Session.Open` 在启动生产线程前安装边界。FFmpeg 可向前定位到一个包，从其真实时间戳和流时间基算出样本位置，丢弃前导，精确裁到源采样结束；在变速、重采样、增强与入 ring 之前保证源输入边界。重采样/DSP 排空只消费本轨输入，沿用现有输出 drain 和结束通知。
3. UI、SMTC、歌词、统计和 scrobble 只看从零开始的单轨时钟。Seek 到曲内 `t` 时在引擎里加起点并夹紧到末端；快照的总时长、重放、暂停恢复和 seek 确认均按同一坐标。会话 EOF 只触发一次自动切歌，旧 epoch 的进度和完成事件失效。
4. `PlaybackCoordinator.Gapless.PreserveAlbumBoundary` 目前把相同路径判为同曲，`PlaybackEngine.Gapless.SameTransition` 也只比较 token/epoch/path。按出现项、版本与区间判断重播和跳转。相邻同镜像同盘轨保留专辑自然边界，乱序跳轨按用户现有过渡设置执行。首版用现有双会话预载打开同文件的不同区间。以输出采样验证自然边界，再决定是否值得复用同一解码器；共享会话不应成为首版依赖。

## 7. 目录封面与异步等待

- 建立本地封面来源解析，优先顺序建议为版本显式覆盖、音频内嵌正面、同目录 `cover`、`folder`、`front`、现有设置允许的网络取图、默认图。目录候选按固定文件名及固定扩展顺序（jpg、jpeg、png），大小写不敏感。本例选择 `cover.jpg`。`back.jpg` 不作自动正面。复用已有内嵌读取器；普通本地单曲同样可用目录图。来源探测设置文件大小和解码像素上限，候选损坏时继续下一个。
- 扫描只记录图片候选和来源依赖。实际展示才读字节、算内容哈希、缩放、取色。区分“哪个来源的哪一版”与“图片字节的 ImageHash”。源修订考虑文件路径、大小和 UTC 时间；收到图片替换事件时强制重新核对内容，避免等长覆盖和保留时间戳导致假命中。应用关闭时的变化在下次扫描校验，需识别等长等时替换的场景可对候选图按目录只算一次内容哈希。
- 在 `GetRawImage`、`CoverLoadQueue`、`CoverPresentationService` 的缓存捷径之前验证同一来源修订。已知目录图的并发请求按来源/修订合并，缩略图再按像素尺寸区分。某一列表项回收只取消自己的等待。尚未取得 ImageHash 时，现有 CacheKey 回退为 id；未入库曲目均为 id:0，需改用临时轨键或已验证的封面来源键。封面切换和扫描提交后把生效的新哈希发布给全部继承该图的子轨；用户单独指定的版本封面保持独立。共享原图用完整临时文件原子发布，可复用 `PlaybackCoverCache.StoreAsync`。一个引用过期时仅解除映射，旧内容交给缓存清理，避免删掉其他专辑仍使用的同哈希文件。
- 播放切换可以先提交音频和文本，封面异步更新；后台完成时核对曲目/出现项、来源修订和取消令牌。列表缩略图、播放栏、详情大图/HDR、桌面歌词背景、歌单代表图、统计代表图、SMTC 均从生效封面取得一致结果。同一张图跨九轨可复用图像与取色结果，SMTC 曲名及单轨时长仍须逐曲提交。验证清缓存、关闭磁盘缓存、当前曲目对象未替换、在途旧图晚到和手工覆盖后重扫。

## 8. 逐轨读写及派生数据

| 功能 | 建议行为与涉及入口 |
|---|---|
| 单曲删除、批量删除 | CUE 子轨只提供符合曲目语义的操作，如从歌单移除。禁用其“从磁盘删除”；在执行层复核共享源，包含整轨旧行和混合多选，防止从其他入口删掉全盘。普通文件删除保持原行为。若以后提供“删除整张源文件”，单列有影响范围的专辑级动作。 |
| 标签与封面编辑 | 首期支持每轨“保存到曲库”。拆开 `CanEditFile` 同时控制的读图、库内编辑、文件写入等能力。单曲写入、专辑批量写入、`PendingMetadataWrite` 入队及重试均按目标能力校验。混合专辑先说明哪些目标可写，避免执行到一半才发现共享源。`MusicDatabaseService.AdditionalMetadata` 的懒读取遵循 CUE 来源优先级，保存草稿时按版本及修订验证。 |
| 歌词 | `LyricsLoader/RefreshService`、`LyricsEditorViewModel`、`LyricsFilePolicy/Exporter`、`OneShotLyricsCache` 改用曲目身份与单轨时长。存储继续沿用 Song 歌词和版本覆盖。建议用“轨号 + 曲名”的独立歌词文件名，清理非法字符并处理碰撞；翻译、发音和一次性缓存同样隔离。整盘同名歌词或嵌入歌词只有在能按时间裁剪并平移时才作为逐轨来源。 |
| 转码与发送 USB | `AudioConverterService`、`FFmpegAudioConverter` 接受同一采样区间。按子轨导出并写入单轨标题、曲号、封面，生成唯一目标名，排除源镜像作为覆盖目标。USB 普通盘和 MTP 使用同一份分轨产物，`UsbExportPaths/Conflicts`、`UsbDeviceMusicPresence` 使用导出映射。分轨失败向用户报告；现有“转码失败回退复制原文件”对 CUE 会意外复制整盘，须在服务层截住。 |
| 分析、推荐与频谱 | `AnalysisRepository.SourceKey`、指纹、推荐读取 SQL、`SpectrogramService.CacheKey`、worker 请求和播放器 `LoudnessScanner` 都纳入源版本与区间。`AudioAnalysisWorker` 的特征窗口以单轨时长选点；频谱、响度同样仅消费对应区间。源/区间改变后过期重算，九首歌不可共享整盘结果；首期暂未适配的消费者明确显示不可用。 |
| 其他来源 | 首期设备侧枚举和 WebDAV 仍走各自管理与可用性规则，USB 导出属于本地曲目的出站操作。后续远程 CUE 需支持 Href 大小写、ETag、Range seek 与离线依赖完整性；MTP 输入另行处理对象 ID、访问及缓存。 |

## 9. 实施顺序与上线验收

| 顺序 | 可评审交付 | 合入前验证 |
|---|---|---|
| 1 | 解析器、源区间、真实样例基准和数据库升级 | 用隔离的 v4 库验证备份独立可恢复、中断重启继续、同代重开不重复备份。保留普通单曲及 WebDAV ID，校验新索引和重复历史路径。 |
| 2 | 目录展开与 watcher、旧整轨可见性、自动选版规则 | 扩展 `FolderScanRegression`、`SongVersionRegression`。覆盖重扫 ID 稳定、改名、调边界、删除/恢复、同号换曲、重复 CUE、分轨单曲副本、相同标题兄弟轨、手动拆分后重启。检查取消时只有完整提交组可见。 |
| 3 | IPC、真实解码区间、队列与相对时钟 | `AudioPlayerSmokeTest`、`PlaybackSwitchRegression`、`AutoAdvanceRegression` 配合真实子进程。用相邻边界有不同信号的自建无损样本，原速原采样率、无 DSP 下比较解码输出与参考切片，验证 seek、尾块和无缝拼接。再覆盖 1.5 倍速、重采样、跨设备、单曲循环、乱序、快速点同路径不同轨、暂停后恢复及尾轨自然结束。 |
| 4 | 封面来源、缓存失效、各页显示、文件操作防护 | `FolderBrowserRegression`、`MusicMetadataRegression`、`PlaylistCoverRegression`、`SmtcCoverRegression`、歌词回归，加发布版 UI 实测。用样例副本替换、删除、恢复 cover.jpg，并读回后台最终发布的图片；验证只有第 2 轨设置覆盖时，其余 8 轨继续跟随目录图。混合多选删除及延迟标签写入后验证源镜像内容未变。 |
| 5 | 外部打开、转码/USB、频谱/分析和发布 | 校验导出第 2 轨的时长、标签、采样边界与文件名；USB 失败无整盘回退，两个子轨的特征/频谱各取自本轨。运行 `UsbTransferRegression`、`AudioAnalysisRegression`、`SpectrogramRegression` 和文件激活/恢复回归。主工程按源码哈希重发 `AudioPlayer.exe` 和 `AudioAnalysisWorker.exe`，提交 exe 与对应 .hash。 |

首个可发布版本将扫描曝光与播放器、删除/写入防护同时交付。性能验收分别记录冷扫与未改动重扫的音频打开次数、图片读取次数、首屏可见时间，以及滚动/快速切歌期间的在途任务数。样例整轨在一次变化扫描中只探测一次；明确的普通目录不能因支持 CUE 而逐曲重复枚举。跨子进程播放、封面等待与 UI 发布应有现场验证。

发布验收使用全新输出目录的 Release/x64 剪裁包，检查新增 SQLite 映射、IPC 序列化与 `x:Bind` 成员保留。核对七种语言独立资源键，运行同一份发布产物的扫描、点播、版本选择、系统媒体封面及恢复操作。功能实现时在 `docs/Changelog.md` 顶部登记。实施后的验证结果见下节。

## 10. 实施与验证记录

- 支持位于同一目录的外置 CUE 与原生 FLAC、RIFF PCM WAV。目录图片依次查找 cover、folder、front，各候选名支持 JPG、JPEG、PNG。保留单轨手选封面优先级。逐轨歌词文件使用“清单名 - 轨号 - 标题”，两份清单可在同目录使用相同的标题和轨号。
- 用真实《Sweet Ices Cream》比对 9 轨端点、8 处相邻交界和最后一个采样。NativeAOT 播放器通过 WASAPI 完成曲内定位、顺播接管和近文件末尾的结束通知。无索引 FLAC 深处定位失败时回退到较早的数据包，从解码时间戳恢复精确边界。播放切换回归 384/384。
- 第 02 轨和第 09 轨分别导出 9,810,192 和 10,074,303 个 44,100 Hz 采样帧。两个文件各自解码后的 PCM SHA-256 都与独立 FFmpeg 裁剪一致，标签及 600 × 600 封面读回一致。
- 隔离数据库验证 v4 升级、独立恢复备份、中断后重试和同代重开。扫描覆盖重扫、改名、坏清单、冲突、删除与恢复，包含无旧整轨时的原子恢复。合成测试中，展开两轨只读取一次音源元数据，未变更重扫未再次读取。版本与迁移回归 128 项通过。
- 目录图覆盖九次并发读取、等长等时间戳替换和损坏候选回退。真实 WinUI 封面控件、系统媒体控件、歌单卡片及声谱图窗口使用隔离数据运行。M3U8 导出保留轨号和起止，并在回读时固定对应版本。
- 发布验收使用 Release/x64 和项目的剪裁配置，两份 NativeAOT 子进程按源码重新发布。未连接实体 MTP 设备，USB 导出以磁盘路径验证。未在用户现有曲库上演练升级，也未量化大库首屏耗时、滚动峰值或整机内存。

最终剪裁主程序集通过 20 项隔离检查，含两种按 CUE 轨号、按物理音源的显式查询。MSIX 清单包含 .cue 关联，包内主程序集与两个 NativeAOT 可执行文件的 SHA-256 均与验证目录一致。安装包沿用项目的未签名配置。
