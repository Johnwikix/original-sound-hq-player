# 歌曲页内详情、双向连接动画与轮播外观保持验证

日期：2026-10-31。最终实现保留 Frame 与既有跨页动画；本功能不导航 Frame，不修改当前自编译 CsWinRT 或播放器协议。

## 2026-10-31 追加：封面生命周期与双向交接时序

本轮使用 diagnose、winui-design、lvt 技能，读取用户指定的 `C:\Users\90684\Documents\OriginalSoundPlayer\Logs\WinUIMusicPlayer-20261031.log`，在现有独立原生夹具中复现后修改生产实现。没有启动或操作用户正在使用的生产窗口，没有更改 Carousel 的尺寸、位置、圆角或材质。

### 已确认的问题与修复

1. **缩略图与圆角一起消失**：真实原生加载循环出现 `Loaded → Loaded → Unloaded`，最后一个回调到达时 `IsLoaded`、内部内容的 `IsLoaded` 均为 true。旧卸载回调清掉了新图片、加载令牌和合成 Clip；这直接解释占位符与方角同时出现。现在只有确认已经离树才清理，旗标未更新时排队复核，重新挂入的控件保留新资源。旧事件记录位于 `artifacts/home-polish-batch2/hover-cycle-repro/cover-cycle-events.json`，修复后同类记录位于 `artwork-detail-final/cover-cycle-events.json`。
2. **取消与损坏缓存**：用户日志存在 150px 缓存过小及取消解码错误，而悬浮面板的较大图片仍能加载。基线直接运行实际 WIC 解码方法，已取消令牌仍被吞成成功，并留下 54 字节 BMP 文件。现在取消继续抛出；缓存先写同目录独立临时文件，写完、刷新、关闭后再替换最终文件，失败清理临时文件。缓存读者允许文件被替换，原有缓存格式不变。没有清空用户缓存，旧损坏文件仍由既有校验逻辑按需重建。
3. **悬浮面板退出末帧闪烁**：原先动画结束后先 Restore 到全尺寸、不透明，再 Collapsed；基线观测到关闭后的合成 Opacity 为 1。现在 Hide 保持透明终态，下次 Reveal 才复位。滚动回调原先直接 Dismiss；改走原来的 200ms CollapseToCover，保留图片到实际合成动画完成，重复滚动不重启动画。离页和尺寸变化仍及时清理。
4. **详情切换过早隐藏与样式闪烁**：基线标题点击的同一调用内来源 Opacity 已经为 0，来源根 IsEnabled 为 false，标签 TextBox IsEnabled 也为 false；连接动画却要等之后的排队回调才 TryStart。现在让目标以近透明状态完成布局并经过两次实际 Rendering 回调，再在同一 UI 回调 Prepare/TryStart 封面与标题，随后交接内容透明度。返回同样预先恢复来源渲染。内容用 150ms 透明度交接，没有平移、缩放整页或 Frame Entrance。
5. **交互与清理**：来源不再禁用整树，只禁止命中；详情聚焦标题并使用 Cycle 限制 Tab 范围。九个标签 TextBox 加载中只读，加载完成后可编辑，远程歌曲继续只读。渲染等待由 presenter 持有，返回、请求替换、离页和 Dispose 均解绑；准备中返回不显示详情，进入中返回直接反向。仍保留同一 Frame、Page 和 ListView，详情没有全局单例或导航缓存。

### 本轮证据

- 失败基线：`artifacts/song-details/hover-artwork-before.log`、`hover-cycle-repro.log`、`detail-styles-before.log`。最初用程序 ChangeView 触发滚动的用例没有触发 ViewChanging，超时不能算作产品故障证据；最终用例调用实际 XAML 绑定的滚动处理器，并观察真实原生合成退出。
- 最终主项目 x64 Debug：0 错误、381 个警告，`artifacts/song-details/artwork-detail-main-final.log`。独立夹具 0 错误、29 个警告；没有改动播放器或 IPC，无需重新发布 AudioPlayer。
- 最终原生/lvt：**163 项检查、20 次定向 UIA Invoke、三轮诊断 GC、前台违规 0**。`artifacts/home-polish-batch2/artwork-detail-final/result.json` 与 `artifacts/song-details/artwork-detail-final.log`。复跑：`./_tools/HomeUiRegression/Run-Polish.ps1 -RunName artwork-detail-final`。
- 封面检查涵盖反复移出/挂入后图片与 Clip 保留、预取消和解码启动后取消、完整缓存输出、目标文件被锁时保留旧缓存且无临时文件残留。锁文件测试故意触发一次写缓存 IOException，属于故障注入，不能解读为真实用户缓存写入失败。
- 详情检查涵盖真正的封面与标题正反向 Completed、TextBox 原生模板已经实现且加载中保持启用/只读、来源根 IsEnabled 变化为 0、相同来源实例/行/滚动偏移/选择、真实 SQLite 保存与迟到读取隔离。另验证渲染前返回、连续替换准备请求、进入动画中返回、准备期间卸载；实际 Rendering 记录在 `artwork-detail-final/detail-render-timeline.json`。
- 三轮 GC 均确认退休的 **7 个详情 panel**、循环重载后移出的封面可回收。31 个首页中保留第一个 UIA 观察对象，其他 30 个在最后一轮后均不可达；这沿用夹具的既有观察者隔离规则，不声称 31 个全部回收，也不由此推导显存即时下降。

后台测试隔离实际音频、网络和用户文件；封面解码、控件、模板、渲染回调、合成动画、SQLite 与 GC 是真实实现。为避免影响前台，没有发送真实鼠标滚轮，也没有在生产窗口逐帧录屏。RTB 静态输出与旧基线均存在文本编辑器内容缺失，没有把它当作动画末帧像素验收。因此，这里证明了导致闪烁的状态切换与错误交接时序被移除，不宣称所有 DPI、主题和 GPU 上已经逐帧验证。既有 184 项全量首页旧断言的限制仍见下文，不将本轮 163 项专项混称为该全量通过。

## 最终承载方式

详情是当前页面中的 `MusicDetailsPanel`（UserControl），复用通用 `MusicDetailControl` 和独立 `MusicDetailsViewModel`。歌曲列表、最爱、分组与歌单中的歌曲标题进入同一详情流程，两个播放队列沿用同一展示入口。

来源页从创建时就拥有来源内容与详情两个 presenter。打开详情先准备目标渲染，再交接来源的可见性与交互；来源不切换 IsEnabled，避免整个控件树的 Disabled/Normal 视觉状态过渡；来源仍在可视化树中，既不 Collapsed、不 Unloaded，也不重设 ItemsSource。返回只收起临时详情层，因此保留同一 Page、ListView、已实现的歌曲行控件、滚动位置和选择。

`MusicDetailsPresenter` 不调用 Frame.Navigate/GoBack，不更换 MainFrame.Content，不修改 BackStack，也不再包含主/内层 Frame 的额外过渡抑制逻辑。普通页签和侧栏导航保持原有行为。这里的保留仅持续到关闭详情或离开当前页面；没有为其他已离开的页面恢复缓存，也没有注册全局详情视图单例。

## 连接动画与生命周期

- 列表左侧封面和歌曲标题分别连接到详情的封面和标题；进入使用 Gravity，返回使用 Direct。两组动画均完成或安全取消后，才移除详情并释放其图片绑定。没有叠加整页进入动画。
- 通过弱引用和歌曲标识解析返回目标，必要时查找仍已实现、仍可见的对应行。数据更新导致目标消失时取消相应连接，不强行滚动或连到回收后代表另一首歌的行。
- 隐藏来源时停止该行的悬停文字滚动；首页作为来源时暂停轮播和预览。关闭恢复来源活动。仅真正从该行键盘进入、且按钮完全可见时才恢复键盘焦点，防止 Focus 引发隐式 BringIntoView。
- 离开整个来源页、覆盖请求或目标卸载会取消快照和迟到回调。编辑器开始关闭时取消可取消读取；退出屏障继续等待真实在途任务与已接受写入。没有加入生产 GC.Collect。
- 详情播放使用捕获的来源类型，队列入口保持现有队列并传递具体条目标识；标签和歌词继续使用既有草稿与 SQLite 保存管线。

连接配置依据微软的 [Connected animation 指南](https://learn.microsoft.com/en-us/windows/apps/develop/motion/connected-animation)。通用详情如今完全不发起 Frame 导航，因此无需修改其进入/返回过渡。

## Carousel：修复裁切而保持外观

上一版把按钮移到封面内部，改变了原外观，已撤销。

最终保留按钮相对主图原有的位置：左边界 -10 DIP，右边界在主图右侧再伸出 10 DIP。封面、缩略拼贴、分页点的尺寸及页面坐标、按钮材质与圆角均沿用原定义。

轮播为外伸控件保留 16 DIP 的绘制空间，用等量布局补偿保持图片坐标；箭头移到独立覆盖层，避免被主图内容层裁切。首页横向 24 DIP 留白改为等量 padding，使外伸部分仍处于父内容的实际绘制边界内。

原生回归分别检查 320、600、900 DIP、有/无拼贴六种组合。每次既验证图片原位置与宽度、箭头原有外伸坐标，又在左右两个按钮的左/中/右取样，共 36 个点用原生命中检测确认没有被边界裁掉。它验证的是外观几何保持和完整命中区域，不能代替所有 GPU、DPI、主题下的逐帧像素对照。

## 数值输入与播放选择

11 处 Compact NumberBox 改用 Inline；歌词偏移宽度 176、缓存上限宽度 160，保留既有范围、步长和双向绑定。页内详情已验证数值写回草稿；旧属性窗口、曲线和设置面板的高 DPI/多语言尺寸矩阵未操作验证。

播放选择与滚动独立：跟随待播/确认状态选择当前显示集合中的对应项，取消待播后恢复确认条目。普通列表按引用、标识或外部路径匹配，歌单支持包装项，队列区分重复条目。所有外部订阅只在视图加载期间存在。

## 验证证据与范围

- 主项目 x64 Debug：构建 0 错误、381 个警告，日志 `artifacts/song-details/in-page-main-verified.log`。
- 列表原生/lvt：157 项通过，13 次按钮调用，前台违规 0；保留播放选择、无自动滚动、手动居中及五页浏览位置回归。删除了为“返回重建”编写的两项旧检查。
- 页内详情定向测试：真实列表滚到第 21 行并选中该行，打开、返回、保存后验证相同来源 Page 和行对象、相同偏移与选择；Frame 仅在夹具初始化时导航一次，详情产生 0 次导航和 0 条历史。
- 观察到封面、标题正向及反向四个真实 ConnectedAnimation.Completed；返回配置为 Direct。使用实际通用控件、编辑 ViewModel、歌词仓库和 SQLite 验证保存、独立会话、迟到封面隔离及离页后接受写入的归属。
- 原生首页/详情/轮播专项 119 项通过，15 次定向 lvt 调用，三轮诊断 GC：`artifacts/home-polish-batch2/in-page-verified/result.json`，文本日志 `artifacts/song-details/in-page-verified.log`。包含三轮诊断 GC，退休详情控件均可回收；前台违规 0。
- 修复前 `artifacts/song-details/inline-before.log` 失败于“必须保留同一来源页”，锁定了原先替换 Frame 内容的错误承载方式。最终测试增加了真实滚动偏移、选择和四个方向完成事件的检查。
- 修改文件统一 CRLF；静态校验 XML 和所有语言中的 GetString 资源键。构建不作为运行资源解析的替代证据。

独立夹具用记录参数的音频适配器代替实际播放引擎，封面 I/O 使用受控异步关卡；没有激活、点击用户的生产窗口，也没有测试真实网络/音频设备。本轮未重新宣称既有 184 项首页全量回归通过：之前当前代码与提交前生产代码对照都出现过悬浮重入及 ScrollTo 172 的旧断言失败，记录仍在 `artifacts/song-details/home-full-result.txt`、`home-baseline-result.txt` 和 `home-baseline-scroll-result.txt`。

保持当前来源 UI 是无重建返回的必要成本。它不等于缓存所有页面；关闭详情后释放详情对象，离开来源页后仍走原来的禁用缓存与卸载清理。GC 结果证明对象可达性收尾，不代表整个进程 Private Bytes 或显存即时下降，本轮不宣称具体净内存节省比例。
