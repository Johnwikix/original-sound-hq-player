using System;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    private double _displayPeakNits;
    private bool _hdrPresentationFailed;

    public bool PlaybackHdrEnabled
    {
        get => State.Preferences.PlaybackHdrEnabled;
        set => State.Preferences.PlaybackHdrEnabled = value;
    }

    public double PlaybackHdrPeakNits
    {
        get => State.Preferences.PlaybackHdrPeakNits;
        set => State.Preferences.PlaybackHdrPeakNits = value;
    }

    public double PlaybackHdrReferenceWhiteNits
    {
        get => State.Preferences.PlaybackHdrReferenceWhiteNits;
        set => State.Preferences.PlaybackHdrReferenceWhiteNits = value;
    }

    public double PlaybackHdrWhiteNits => Math.Min(_displayPeakNits, PlaybackHdrReferenceWhiteNits);
    public ulong PlaybackHdrDisplayId { get; private set; }
    public bool PlaybackHdrDisplayAvailable => _displayPeakNits > 0;
    public double PlaybackHdrEffectivePeakNits => Math.Min(_displayPeakNits, PlaybackHdrPeakNits);
    public bool PlaybackHdrActive { get; private set; }

    public string PlaybackHdrStatusText
    {
        get
        {
            if (!PlaybackHdrEnabled) return ToolUtils.GetString("PlaybackHdrOff");
            if (!PlaybackHdrDisplayAvailable) return ToolUtils.GetString("PlaybackHdrDisplaySdr");
            if (!IsFluidBackgroundEnabled && string.IsNullOrWhiteSpace(WindowBackgroundImagePath))
                return ToolUtils.GetString("PlaybackHdrBackdropSdr");
            if (_hdrPresentationFailed) return ToolUtils.GetString("PlaybackHdrFallback");
            if (!PlaybackHdrActive) return ToolUtils.GetString("PlaybackHdrReady");
            return string.Format(ToolUtils.GetString("PlaybackHdrOutput"), PlaybackHdrWhiteNits, PlaybackHdrEffectivePeakNits);
        }
    }

    public string FormatPlaybackHdrNits(double nits) => $"{nits:0} nit";

    internal void SetPlaybackHdrDisplay(ulong displayId, double peakNits)
    {
        bool outputChanged = PlaybackHdrDisplayId != displayId;
        if (!outputChanged && _displayPeakNits == peakNits) return;
        bool wasAvailable = PlaybackHdrDisplayAvailable;
        PlaybackHdrDisplayId = displayId;
        _displayPeakNits = peakNits;
        if (peakNits <= 0) ReportPlaybackHdrPresentation(false, false);
        if (outputChanged) OnPropertyChanged(nameof(PlaybackHdrDisplayId));
        if (wasAvailable != PlaybackHdrDisplayAvailable) OnPropertyChanged(nameof(PlaybackHdrDisplayAvailable));
        OnPropertyChanged(nameof(PlaybackHdrWhiteNits));
        OnPropertyChanged(nameof(PlaybackHdrEffectivePeakNits));
        OnPropertyChanged(nameof(PlaybackHdrStatusText));
    }

    public void ReportPlaybackHdrPresentation(bool active, bool failed)
    {
        active &= PlaybackHdrEnabled && PlaybackHdrDisplayAvailable;
        if (PlaybackHdrActive == active && _hdrPresentationFailed == failed) return;
        _hdrPresentationFailed = failed;
        if (PlaybackHdrActive != active)
        {
            PlaybackHdrActive = active;
            OnPropertyChanged(nameof(PlaybackHdrActive));
        }
        OnPropertyChanged(nameof(PlaybackHdrStatusText));
    }
}
