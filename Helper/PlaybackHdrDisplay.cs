using System;
using Microsoft.Graphics.Display;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace WinUIMusicPlayer.Helper;

/// <summary>Tracks the output containing the largest part of the playback window.</summary>
internal sealed class PlaybackHdrDisplay(AppWindow window, DispatcherQueue dispatcher,
    Action<ulong, double> publish, Action<Exception> reportError) : IDisposable
{
    private DisplayInformation? _display;
    private ulong _displayId;
    private bool _enabled;
    private bool _disposed;

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            window.Changed += OnWindowChanged;
            Update();
        }
        else
        {
            window.Changed -= OnWindowChanged;
            Disconnect();
            publish(0, 0);
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.IsVisible && (args.DidPositionChange || args.DidSizeChange ||
            args.DidVisibilityChange || args.DidPresenterChange))
            Update();
    }

    private void OnAdvancedColorChanged(DisplayInformation sender, object args)
    {
        // A notification for the previous output can already be in the dispatcher queue.
        dispatcher.TryEnqueue(() =>
        {
            if (_enabled && ReferenceEquals(sender, _display))
                Update(readColor: true);
        });
    }

    private void Update(bool readColor = false)
    {
        ulong id = 0;
        double peak = 0;
        try
        {
            var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
            if (area is not null)
            {
                id = area.DisplayId.Value;
                if (_display is not null && _displayId == id && !readColor) return;
                if (_display is null || _displayId != id)
                {
                    Disconnect();
                    _display = DisplayInformation.CreateForDisplayId(area.DisplayId);
                    _displayId = id;
                    _display.AdvancedColorInfoChanged += OnAdvancedColorChanged;
                }

                var color = _display.GetAdvancedColorInfo();
                if (color.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange &&
                    double.IsFinite(color.MaxLuminanceInNits) && color.MaxLuminanceInNits > 0)
                    peak = color.MaxLuminanceInNits;
            }
            else Disconnect();
        }
        catch (Exception ex)
        {
            reportError(ex);
            Disconnect();
        }
        publish(id, peak);
    }

    private void Disconnect()
    {
        if (_display is null) return;
        var previous = _display;
        _display = null;
        _displayId = 0;
        previous.AdvancedColorInfoChanged -= OnAdvancedColorChanged;
        previous.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_enabled) window.Changed -= OnWindowChanged;
        _enabled = false;
        Disconnect();
    }
}
