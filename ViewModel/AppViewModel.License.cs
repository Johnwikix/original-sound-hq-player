using CommunityToolkit.Mvvm.Input;
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

        /// <summary>关于页与 DSP 提示共用购买命令，防止跨页面重复发起购买。</summary>
        public IAsyncRelayCommand PurchaseLicenseCommand { get; }

        /// <summary>试用中或许可非活跃时显示购买入口。</summary>
        public bool CanPurchaseLicense => _licenseService.CanPurchase;

        /// <summary>关于页显示试用剩余天数或许可受限说明。</summary>
        public string LicenseDescription => _licenseService.TrialRemainingDays is int days
            ? string.Format(ToolUtils.GetString("LicenseTrialRemaining"), days)
            : LicenseRestricted ? ToolUtils.GetString("LicenseRestrictedFeatures") : "";

        /// <summary>许可非活跃；具体设置的可用性由 LicensePolicy 决定。</summary>
        public bool LicenseRestricted
        {
            get => field;
            private set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(DsdBitstreamAllowed));
                    OnPropertyChanged(nameof(DsdCardDescription));
                    OnPropertyChanged(nameof(Surround51Allowed));
                    OnPropertyChanged(nameof(AtmosPassthroughAllowed));
                    OnPropertyChanged(nameof(Surround51CardDescription));
                    OnPropertyChanged(nameof(AtmosCardDescription));
                    OnPropertyChanged(nameof(OutputLicenseRestricted));
                }
            }
        }

        /// <summary>DSD Dop/Native 开关可用性：试用受限时锁定。</summary>
        public bool DsdBitstreamAllowed => !_licenseService.IsFeatureRestricted(LicenseFeature.DsdBitstream);

        /// <summary>5.1 输出仅在许可不受限时可编辑。</summary>
        public bool Surround51Allowed => !_licenseService.IsFeatureRestricted(LicenseFeature.Surround51);
        /// <summary>Atmos HDMI 直通仅在许可不受限时可编辑。</summary>
        public bool AtmosPassthroughAllowed => !_licenseService.IsFeatureRestricted(LicenseFeature.AtmosPassthrough);
        public bool OutputLicenseRestricted => _licenseService.IsFeatureRestricted(LicenseFeature.AllOutput);
        /// <summary>5.1 卡片保留正常说明，受限时显示购买提示。</summary>
        public string Surround51CardDescription => ToolUtils.GetString(!Surround51Allowed
            ? "LicenseSurround51Locked" : "ExperimentalSurround51Description");
        /// <summary>Atmos 卡片保留兼容性说明，受限时显示购买提示。</summary>
        public string AtmosCardDescription => ToolUtils.GetString(!AtmosPassthroughAllowed
            ? "LicenseAtmosLocked" : "ExperimentalAtmosDescription");

        /// <summary>DSD 卡片描述：受限时替换为锁定提示。</summary>
        public string DsdCardDescription => !DsdBitstreamAllowed
            ? ToolUtils.GetString("LicenseDsdLocked")
            : ToolUtils.GetString("DsdDopDescription");

        private void ApplyLicenseState()
        {
            if (_isDisposed) return;
            LicenseRestricted = _licenseService.IsRestricted;
            OnPropertyChanged(nameof(CanPurchaseLicense));
            OnPropertyChanged(nameof(LicenseDescription));
            PurchaseLicenseCommand.NotifyCanExecuteChanged();
        }
    }
}
