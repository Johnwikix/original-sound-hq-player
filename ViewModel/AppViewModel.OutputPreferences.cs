using BassPlayerIpc.Shared;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    /// <summary>恢复磁盘中的高级输出偏好，不应用用户编辑的许可守卫。</summary>
    /// <param name="saved">已加载的音频偏好。</param>
    internal void RestoreOutputPreferences(AudioPreferences saved)
    {
        // 冷路径直接更新唯一状态源；许可仅覆盖 IPC 副本，不能丢弃启动时加载的值。
        var audio = State.Preferences.Audio;
        audio.IsDopEnabled = saved.IsDopEnabled;
        audio.ExperimentalSurround51 = saved.ExperimentalSurround51;
        audio.ExperimentalAtmosPassthrough = saved.ExperimentalAtmosPassthrough;
    }

    /// <summary>Gets or sets the saved DSD bitstream preference when licensed.</summary>
    public bool IsDopEnabled
    {
        get => State.Preferences.Audio.IsDopEnabled;
        set
        {
            if (!DsdBitstreamAllowed)
            {
                OnPropertyChanged(nameof(IsDopEnabled));
                return;
            }
            State.Preferences.Audio.IsDopEnabled = value;
        }
    }

    /// <summary>Gets or sets the saved 5.1 output preference when licensed.</summary>
    public bool ExperimentalSurround51
    {
        get => State.Preferences.Audio.ExperimentalSurround51;
        set
        {
            if (!Surround51Allowed)
            {
                OnPropertyChanged(nameof(ExperimentalSurround51));
                return;
            }
            State.Preferences.Audio.ExperimentalSurround51 = value;
        }
    }

    /// <summary>Gets or sets the saved Atmos passthrough preference when licensed.</summary>
    public bool ExperimentalAtmosPassthrough
    {
        get => State.Preferences.Audio.ExperimentalAtmosPassthrough;
        set
        {
            if (!AtmosPassthroughAllowed)
            {
                OnPropertyChanged(nameof(ExperimentalAtmosPassthrough));
                return;
            }
            State.Preferences.Audio.ExperimentalAtmosPassthrough = value;
        }
    }
}
