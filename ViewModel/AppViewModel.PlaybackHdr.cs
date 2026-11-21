using System;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    private double _displayPeakNits;

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
    }

    public void ReportPlaybackHdrPresentation(bool active, bool failed)
    {
        active = active && !failed && PlaybackHdrEnabled && PlaybackHdrDisplayAvailable;
        if (PlaybackHdrActive == active) return;
        PlaybackHdrActive = active;
        OnPropertyChanged(nameof(PlaybackHdrActive));
    }
}
