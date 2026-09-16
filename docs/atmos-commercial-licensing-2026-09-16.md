# Atmos HDMI 直通收费：公开资料核查

核查日期：2026-09-16。这里记录工程事实和公开条款，不能替代针对发行地区、专利和具体合同的法律意见。未联系 Dolby 或 HDMI LA，未取得授权豁免确认。

## 当前实现

- `External/AudioPlayer/Decode/Eac3BitstreamReader.cs` 使用 FFmpeg 解封装，将 E-AC-3 access units 封装为 IEC 61937 bursts，保留 JOC 数据；输出路径不负责 Atmos 对象解码/渲染，也不把 PCM 编码成 Atmos。
- 当前接收条件是 E-AC-3、48 kHz、6 声道；由 WASAPI 独占输出。不能宣传为支持所有 Atmos 格式或 TrueHD/Atmos、MAT 编码。
- 接收端硬件负责位流解码。微软说明 Windows 支持通过 IEC 61937 向 HDMI/DisplayPort 接收端传输编码音频；这说明技术可用性，不是第三方知识产权授权证明。[微软文档](https://learn.microsoft.com/en-us/windows/win32/coreaudio/representing-formats-for-iec-61937-transmissions)
- 5.1 开关属于多声道 PCM 输出，不等于 Dolby 编码功能；但输入文件的解码器仍需单独考虑。
- 整个发行包仍包含 FFmpeg AC-3/E-AC-3 解码器，见 `Libraries/FFmpeg/x64/BUILD_INFO.txt`。不能因为直通输出路径不渲染 Atmos，就断言整个产品不涉及音频编解码许可。

## 可确认和不能确认的事项

1. **不能确认收费直通软件免 Dolby 授权。** Dolby 公开流程包含申请、签署协议、费用以及产品测试批准，但没有在所查页面给出针对本项目这类第三方软件位流直通的明确豁免。是否需要合同、专利许可或测试，要提交具体实现和销售地区确认。收费本身不能直接推出违法，免费也不能直接推出豁免。[Dolby 授权流程](https://professional.dolby.com/licensing/)
2. **个人 Dolby Access 购买不等于开发商商业授权。** 该协议授予个人、非商业、不可转让的软件使用许可，并明确未授予 Dolby Atmos 名称、商标或技术的权利。这份消费者协议也不能单独用于证明独立开发的直通软件被禁止。[Dolby Access EULA](https://www.dolby.com/about/legal/terms-of-service/dolby-access/)
3. **商标与技术许可需分别核对。** 在未获确认前，不使用 Dolby/Atmos 官方 Logo，不声称“Dolby 官方认证”“购买 Dolby 授权”。对格式兼容性的文字描述是否获允许，仍取决于适用法律及权利方规则；换名或加免责声明不会自动消除技术授权问题。[Dolby 授权说明](https://professional.dolby.com/licensing/)
4. **FFmpeg 开源义务与专利问题相互独立。** 官方列出动态链接、对应源代码、版权许可告知等合规路径，也指出商业产品的专利问题取决于司法辖区。当前记录的 LGPL 构建不能作为 E-AC-3/Atmos 专利清权证明。本次未对整个发行包做完整 LGPL 审计。[FFmpeg 法律说明](https://ffmpeg.org/legal.html)
5. **商店审核仍涉及知识产权与准确宣传。** Microsoft Store 政策 11.2 要求内容及元数据拥有相应权利或合法使用依据；10.1 要求功能及限制描述准确。商店通过审核不能替代 Dolby 授权确认。[Microsoft Store 政策](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
6. **HDMI 需按产品角色确认。** HDMI Adopter 页面包含规范使用和商标许可。本项目是在既有 Windows 驱动/显卡上运行的应用，不能直接套用硬件制造商费用，也不能从公开概览认定软件一律豁免；如要使用 HDMI Logo 或需要明确结论，可向 HDMI LA 描述产品角色咨询。[HDMI Adopter](https://www.hdmi.org/adopter/index)

## 发布建议

准确功能描述可采用：“E-AC-3/JOC 位流直通（HDMI；需兼容接收设备；当前支持 48 kHz、6 声道输入）”。该描述仍需经过商业授权核对；不要把播放器完整版的购买描述成购买 Dolby 许可证。

正式将此能力作为收费卖点前，向 Dolby 授权部门提供以下说明并请求书面结论，必要时由知识产权律师结合发行地区核查：

> We develop a paid Windows desktop audio player distributed through Microsoft Store. The player uses FFmpeg to demux E-AC-3/JOC streams and encapsulates the original access units into IEC 61937 bursts for WASAPI-exclusive HDMI output to a compatible receiver. This output path does not render Atmos objects or encode PCM to Atmos. The application also ships FFmpeg AC-3/E-AC-3 decoders for PCM playback. Does this implementation require a Dolby technology/patent license or certification in our intended distribution territories? What wording and trademark usage are permitted when describing format compatibility, and are there any special conditions for making this feature part of the paid version?

补充公司主体、商店链接、目标地区、预计销量和完整编解码依赖。以上为询问草稿，尚未发送。
