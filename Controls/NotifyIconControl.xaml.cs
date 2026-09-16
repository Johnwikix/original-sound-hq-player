using Microsoft.Extensions.DependencyInjection;
using System;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Controls;

public sealed partial class NotifyIconControl : Microsoft.UI.Xaml.Controls.UserControl, IDisposable
{
    public TrayViewModel ViewModel { get; }
    public NotifyIconControl()
    {
        ViewModel = App.Services.GetRequiredService<TrayViewModel>();
        InitializeComponent();
    }
    public void EnsureCreated()
    {
        Bindings.Update();
        if (!NotifyIcon.IsCreated) NotifyIcon.ForceCreate(enablesEfficiencyMode: false);
        if (!NotifyIcon.IsCreated) throw new InvalidOperationException("Unable to register the notification area icon.");
    }
    public void Dispose()
    {
        ViewModel.Dispose();
        NotifyIcon?.Dispose();
        NotifyIcon = null;
    }
}
