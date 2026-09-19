using BassPlayerIpc.Shared;

namespace WinUIMusicPlayer.Utils;

internal static class AtmosText
{
    public static string Failure(AtmosFailure reason) => reason switch
    {
        AtmosFailure.UnsupportedFormat => ToolUtils.GetString("AtmosUnsupported"),
        AtmosFailure.DeviceUnavailable => ToolUtils.GetString("AtmosDeviceUnavailable"),
        AtmosFailure.DeviceBusy => ToolUtils.GetString("AtmosDeviceBusy"),
        AtmosFailure.ExclusiveDenied => ToolUtils.GetString("AtmosExclusiveDenied"),
        AtmosFailure.DecodeFailed => ToolUtils.GetString("AtmosDecodeFailed"),
        AtmosFailure.SelectDevice => ToolUtils.GetString("AtmosSelectDevice"),
        AtmosFailure.OutputFailed => ToolUtils.GetString("AtmosOutputFailed"),
        _ => ""
    };
}
