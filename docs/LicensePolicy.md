# 许可功能门控

收费功能清单位于 `Services/LicensePolicy.cs` 的 `RestrictedFeatures`。
目前为卷积校正、DSD 位流、5.1 和 Atmos；响度归一化、增益、平衡、声道交换、单声道、交叉馈送、立体声宽度不受限。

调整已接入功能的范围：在该清单中添加或删除对应的 `LicenseFeature`，重新构建即可。
UI 可用性、ViewModel 写入保护和发往播放端的快照均读取这份清单。它是开发期产品配置，不是用户偏好或运行时热更新开关。

新增一种功能：

1. 在 `LicenseFeature` 添加独立标志，并归入对应的分组。
2. ViewModel/命令通过 `LicenseService.IsFeatureRestricted` 查询；显示状态须随许可事件更新。
3. 在播放端推送边界定义受限时的行为；DSP 对应 `ApplyDspRestrictions`。
4. 批量编辑/重置涉及该功能时，在 `PreserveRestrictedPreferences` 中保留其原有偏好。
5. 添加许可受限、恢复、偏好保留和其他功能不受影响的回归用例。

许可服务负责 Store 状态与已知试用期限。试用与正式许可不受限；许可非活跃、无有效许可和查询失败均按受限处理（fail-closed），后续查询失败保留已确认的有效许可；试用按已知到期时间在本地转为受限，过期缓存不能重新解锁。
许可按包身份（PFN）+ 账户判定，与二进制签名无关：仓库 manifest 即 Store 发布者，旁加载/未签名自建包与商店版同 PFN，已购机器上的自建包启动即完整版。
许可覆盖文件 `Documents\OriginalSoundPlayer\license.debug`（内容 full/trial/expired，删除即恢复真实判定）：DEBUG 构建对任意包生效；Release 仅对未签名包（`Add-AppxPackage -Register` 注册的构建布局）生效，作为本地自用完整版入口，其余签名包不读取、一律查询商店。
启动通过 `AppViewModel.RestoreOutputPreferences` 将磁盘偏好恢复到共享状态，不经过用户编辑守卫；受限时拒绝开启和关闭的编辑，`SettingsSnapshotFactory.CaptureAudio` 始终保存原始偏好。限制只覆盖 IPC 副本，不落盘，购买或恢复许可后重新推送原设置。DSP 总开关和 EQ 不在当前可配置门控项中。

回归：`dotnet run --project _tools/LicenseRegression -c Release`、`dotnet run --project _tools/CurvePresetRegression -c Release`。
