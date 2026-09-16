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

许可服务只负责 Store 状态。试用中、正式许可、非 Store 渠道和首次查询失败的既有规则不变。
限制覆盖不落盘，购买或恢复许可后重新推送原设置。DSP 总开关和 EQ 不在当前可配置门控项中。

回归：`dotnet run --project _tools/LicenseRegression -c Release`、`dotnet run --project _tools/CurvePresetRegression -c Release`。
