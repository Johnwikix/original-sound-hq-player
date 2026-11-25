# 曲库歌曲页签首次显示耗时分析（2026-11-25）

基线为 `5947b25e9a4fcd5732c6dc7b7eef998b7d24e039`，文中行号均对应此提交。

现象：启动后第一次在 `MusicBrowsePage` 点击歌曲页签，从点击到列表出现约等 0.5–2 秒，之后在同一进程内切回基本即时。曲库规模为 5000 首以上，怀疑耗时随歌曲数增长。

采集机为 Windows 26300（10.0.26300），.NET SDK 11.0.100，运行时 11.0.0，主屏 1920×1200、150% 缩放。测量全部在仓库外的隔离基准上完成，未对运行中的应用采样，也未修改生产代码。

## 结论

耗时来自“首次打开歌曲页”的一次性冷成本，而不是 ListView 在处理 5000 条数据，也不是排序。

1. 歌曲行模板 `MusicListRowControl` 较重，每个 `ListViewItem` 子树约 72 个元素。首次实例化时约 10 ms/行，之后每行不到 1 ms。按视口一次生成约 18 行，冷填充到首帧约 190 ms，热填充 14–33 ms（中位约 16 ms）。
2. 创建歌曲页本身（导航到空列表的首帧）另需约 120–220 ms，同样只在首次发生。
3. 列表虚拟化正常。无论 500、5000 还是 20000 条，已生成的行容器数和填充耗时都相同，曲库条数不影响点击页签的耗时。
4. 标题排序在 5000 首时为 4–7 ms。一次性的整理器初始化为 zh-CN 60 ms、ja 93 ms，目前只在从“最爱”页签启动时预热。
5. 代码中唯一随条数明显变慢的是扫描收尾的 `ObservableListUpdates.Apply`，每条通知约 0.13 ms。它发生在扫描结束时，与点击页签无关。

## 测量方法

排序基准是 net11.0 Release 控制台程序（ICU 模式），比较逻辑与 `CjkStringComparer.Compare` 及 `SongRow.Comparer` 的 `DefaultOrder` 分支逐字一致，行结构体的字段布局与生产一致。已校验比较确实走区域排序（“爱”排在“歌”前，与序号比较相反）。标题为合成数据：58% 汉字、32% 拉丁、10% 假名，其中 40% 的汉字标题带常见前缀。

UI 基准在 `_tools/ManualLocationUiRegression` 的副本上加入生产的 `MusicListRowControl`（XAML 与代码后置）、`AutoScrollView`、`AlbumCoverBehavior`，以及与应用 `App.xaml` 相同顺序合并的 `CustomFontStyle`、`BtnStyle`、`FloatingCommandStyle`、`ComboBoxNMenuFlyoutStyle`、`Themes/Generic.xaml`。生产的 `MusicBrowsePage.xaml.cs`、`SongListPage.xaml`、`PlaybackSelectionTracker`、`BulkObservableCollection`、`ObservableListUpdates` 均以链接方式编入。数据为合成，`RefreshDataSource`、封面加载和外壳为桩。

时间口径：

- 填充：调用 `FillFrom`（一次 Reset 通知，与投影发布入口相同）的同步耗时。
- 行布局完成：自触发起，到首个已加载且高度大于 0 的 `ListViewItem` 出现的那次布局结束。
- 首帧：其后第一个 `CompositionTarget.Rendering`。

窗口为 1920×1200 物理像素，不激活并置于最底层，对应主屏最大化。列表视口为 1265×651 DIP，可见第 0–13 行。

## 列表显示与条数无关

视口 1265×651 DIP（可见第 0–13 行）：

| 条数 | 已生成的行容器 | 冷填充到行布局完成 | 热填充到行布局完成 |
| --- | ---: | ---: | ---: |
| 5000 | 18 | 170–207（11 次，中位 181） | 14–33（11 次，中位 16） |
| 20000 | 18 | 未测量 | 18–21（4 次） |

单位均为毫秒。冷填充指进程内第一次填充。5000 条的冷填充到首帧为 180–217 ms，中位 191 ms。

较小视口 839×419 DIP（可见第 0–8 行），单次运行：

| 条数 | 已生成的行容器 | 冷填充到行布局完成 | 热填充到行布局完成 |
| --- | ---: | ---: | ---: |
| 500 | 15 | 未测量 | 11.5 |
| 5000 | 15 | 152 | 31.1 |
| 20000 | 15 | 未测量 | 18.3 |

两个视口下，三个条数生成的行容器数都相同，差异只来自视口高度。离开歌曲页签再返回缓存页面，到首帧约 91 ms，行容器保持已生成状态。

## 冷填充成本构成

同一场景下逐项替换行模板的组成部分，各测 3 次，5000 条，冷填充到行布局完成。

| 行模板变体 | 元素/行 | 冷填充 | 热填充 |
| --- | ---: | ---: | ---: |
| 生产完整模板 | 72 | 170–207 | 14–33 |
| 去掉容器样式（无右键菜单） | 72 | 180–184 | — |
| 3 个 `AutoScrollView` 换成 `ContentPresenter` | 54 | 146–162 | 14–18 |
| 7 个 `Button` 换成 `ContentControl` | 65 | 114–123 | 12–14 |
| 默认容器 + 纯文本行 | 3 | 37–39 | 8–11 |

单位均为毫秒。容器样式中的右键菜单没有可测的成本。与纯文本行相比，生产行模板的冷成本约为 145 ms，其中 7 个 `Button` 约 60 ms，3 个 `AutoScrollView` 约 30 ms，其余（`UserControl`、约 30 个编译绑定、12 列 `Grid`、图片与行为）约 55 ms。各项由各自进程的中位数相减得到，为近似归属。

冷填充 5000 条分配约 6.2 MB，约 0.35 MB/行。冷填充与热填充的中位数相差约 165 ms，说明冷成本主要是一次性的模板加载和绑定代码 JIT，而不是逐行的常规工作。

导航到空的歌曲页：`Navigate` 返回 75–83 ms，页面加载完成 122–127 ms，首帧 125–131 ms。进程的第一次运行为 220 ms。

## 投影、排序与扫描收尾

| 项目 | 结果 |
| --- | --- |
| 标题排序 1000 条 | 0.7–2.1 ms（约 1.0 万次比较） |
| 标题排序 5000 条 | 4.1–7.0 ms（约 6.6 万次比较） |
| 标题排序 10000 条 | 9.0–9.5 ms（约 15 万次比较） |
| 标题排序 20000 条 | 20.3–21.9 ms（约 31 万次比较） |
| 单次区域比较（忽略大小写） | 汉字–汉字约 37 ns，拉丁–拉丁约 27 ns |
| 整理器首次比较 | zh-CN 59.7 ms，ja 92.5 ms |
| 扫描收尾，5000 条基础上追加 500 条 | 逐项 `Add` 16–20 ms；`Apply` 69–86 ms，到首帧 77–94 ms |
| 扫描收尾，5000 条基础上追加 2000 条 | 逐项 `Add` 63–76 ms；`Apply` 240–276 ms，到首帧 249–284 ms |

扫描收尾一项使用生产的 `AppendSongsBatch` 语义（逐项 `Add`）和生产的 `ObservableListUpdates.Apply`。追加的标题交错在已有行之间，每条都需要单独移动位置，因此每条约 0.13 ms。

## 相关代码

- `View/MusicBrowsePage/MusicBrowsePage.xaml.cs:95-124`：`ShowSelectedTab` 按需创建页签页，依次激活、切换 `SwitchPresenter`、刷新数据源。
- `View/SongListPage.xaml:15-25`：容器样式及右键菜单；`:37`：`ItemsStackPanel CacheLength="2"`；`:42`：行模板。
- `View/Controls/MusicListRowControl.xaml:26-202`：7 个 `Button`（第 26、59、75、85、108、164、183 行），3 个 `AutoScrollView`（第 79、95、118 行）。
- `Services/LibraryProjectionService.cs:122-199`：`RefreshSongsAsync`。148-166 在 UI 线程复制字段，167-175 在后台筛选排序，181/183 发布结果（`Apply` 或 `FillFrom`）。254 为默认排序分支。
- `Utils/CjkStringComparer.cs:16-36`：区域比较器与分桶比较。
- `Model/ObservableListUpdates.cs:11-67`：逐项 `Insert`/`Move`/`Remove` 的差异发布。
- `ViewModel/AppViewModel.cs:575-595`：`AppendSongsBatch` 逐项 `Add`。
- `ViewModel/AppViewModel.cs:469-473`、`View/FavouritePlayListPage.xaml.cs:120-124`：整理器预热，仅在当前页为最爱页时触发。

## 修复方向（未实施）

1. 预热：启动空闲时或浏览页停在别的页签时，提前实例化一次行模板和歌曲页，点击时走热路径。同时把排序预热扩展到“最爱”以外的起始页签。
2. 行模板瘦身：操作按钮改为悬停或选中时再创建；`AutoScrollView` 仅在文本溢出时启用。
3. 扫描收尾：大批量追加时用一次 Reset 代替逐项通知。

## 局限与未验证项

- 绝对值是下限。封面管线、投影服务和真实外壳（`NavigationView`、`Frame`）未计入；`Music` 是无属性通知的桩。
- UI 基准沿用了回归工具的 Windows App SDK 2.5.1，应用为 2.7.0。
- 未对真实应用采样，0.5–2 秒中未被隔离基准覆盖的部分没有归因。
- 未观察到条数对点击页签的影响。如果实际存在“曲库越大首次越慢”，原因不在上述路径，可能的间接因素是启动期后台任务（封面、元数据、线程池）的争用，或启动时恰有扫描进入收尾的 `Apply`；两者都未验证。
- 冷数据是每进程一次的样本，数量为 3–11 次，文中给出范围和中位数。

## 复现

两项基准都未入库，临时目录为 `C:\Users\90684\AppData\Local\Temp\songsort-bench-Wypkky`。若需重建：UI 基准以 `_tools/ManualLocationUiRegression.csproj` 为基础，改用绝对路径链接生产文件，去掉工具自带的 `NuGet.Config`（它指向本机不存在的 G:、I: 盘），加入 `Microsoft.Xaml.Behaviors.WinUI.Managed` 3.0.1 与 `Microsoft.Graphics.Win2D` 1.4.0，再按上文所列加入行控件及其资源。运行时设置 `SONG_LIST_BENCH=5000,20000`（首个值为冷填充条数），`SONG_LIST_BENCH_SIZE=1920,1200`（窗口物理像素）。结果写入输出目录下 `artifacts/song-list-bench.txt`。
