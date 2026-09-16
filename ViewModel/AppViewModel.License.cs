using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using System;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        private readonly LicenseService _licenseService;
        private DispatcherQueueHandler? _licenseStateChangedHandler;

        /// <summary>许可受限（试用已到期）：DSD 位流锁定，DSP 仅保留均衡器。</summary>
        public bool LicenseRestricted
        {
            get => field;
            private set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(DsdBitstreamAllowed));
                    OnPropertyChanged(nameof(DsdCardDescription));
                }
            }
        }

        /// <summary>DSD Dop/Native 开关可用性：试用受限时锁定。</summary>
        public bool DsdBitstreamAllowed => !LicenseRestricted;

        /// <summary>DSD 卡片描述：受限时替换为锁定提示。</summary>
        public string DsdCardDescription => LicenseRestricted
            ? ToolUtils.GetString("LicenseDsdLocked")
            : ToolUtils.GetString("DsdDopDescription");

        private void ApplyLicenseState()
        {
            LicenseRestricted = _licenseService.IsRestricted;
        }
    }
}
