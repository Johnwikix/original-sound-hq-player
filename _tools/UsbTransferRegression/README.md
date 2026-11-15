# USB / MTP 实机回归

先连接并解锁安卓手机，保持 USB 文件传输模式。默认命令只验证设备发现和存储标识：

```powershell
dotnet run --project _tools/UsbTransferRegression -c Release
```

传入 `--copy` 后，在手机 MUSIC 下创建唯一 `OriginalSound-UsbRegression-<GUID>` 目录，发送测试音频和歌词，校验内容、覆盖、失败回退、扫描及取消，最后仅删除该测试目录。测试期间保持手机解锁，避免同时运行其他回归或清理程序。

```powershell
dotnet run --project _tools/UsbTransferRegression -c Release -- --copy
```

`--log <绝对路径>` 将结果写入日志；完整复制回归成功必须出现 `PASS: all USB integration checks completed.`。

`invalid` 编码格式和不可用存储是预期失败输入，日志中的相应 Error 用于验证回退与重试；以进程退出码及最终 PASS 标记判断回归结果。

设备发现、MTP 文件操作、歌词导出、FFmpeg 转码、MUSIC 扫描和 SQLite 台账均链接生产源码。现场生成静音 WAV，验证真实 MP3/AAC 编码，并把手机 MP3 读回检查嵌入标签；歌词内容和封面来源使用隔离数据，不访问用户曲库。

覆盖扫描与发送同时排队、等待期间取消、失败后重试，以及台账与扫描的并发提交、数据库重开、空 MTP 标签保留和删除同步。SQLite 使用临时单表数据库。尚需人工覆盖传输中物理拔线、USB 模式切换、手机锁屏/授权变化和多存储设备。

遇到 `0x802A0006` 时，按微软 [WPD 错误码说明](https://learn.microsoft.com/en-us/windows/win32/wpd_sdk/error-constants) 将其视为设备会话不响应，不直接归因于剪裁、权限或锁屏。先结束在途测试，重新连接手机再复测，不能并发删除正在扫描的测试目录。
