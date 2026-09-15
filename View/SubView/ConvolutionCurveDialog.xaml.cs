using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Controls;
using WinUIMusicPlayer.View.SubView.Settings;

namespace WinUIMusicPlayer.View.SubView;

// View-only responsibilities: dialog lifecycle, adaptive layout and pointer-to-VM forwarding.
public sealed partial class ConvolutionCurveDialog : ContentDialog
{
    public ConvolutionCurveViewModel ViewModel { get; }
    public ConvolutionCurveDialog(DspSettingsViewModel bindings)
    {
        ViewModel = App.Services.GetRequiredService<ConvolutionCurveViewModel>();
        InitializeComponent();
        DeviceBindingsHost.Content = new DspDeviceBindingsControl(bindings);
        bindings.BeginCorrectionEditing(() => ViewModel.Draft, ViewModel.LoadCorrectionDraft, ViewModel.RefreshOutputCorrection);
        ViewModel.PreviewChanged += RefreshPreview;
        ViewModel.PresetSaved += () => SaveFlyout.Hide();
        ViewModel.PresetDeleted += () => DeleteFlyout.Hide();
        Response.PointSelected += index => ViewModel.SelectedNode = index;
        Response.PointMoved += ViewModel.MovePoint;
        Opened += async (_, _) => { ResizeEditor(); XamlRoot.Changed += RootChanged; await ViewModel.OpenAsync(); };
        Closing += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = !await ViewModel.CloseAsync();
                if (!args.Cancel) { XamlRoot.Changed -= RootChanged; bindings.EndCorrectionEditing(); }
            }
            finally { deferral.Complete(); }
        };
    }
    private void RefreshPreview()
    {
        Response.SetPoints(ViewModel.Points, ViewModel.SelectedNode);
        Response.Refresh();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeEditor();
    private void ResizeEditor()
    {
        EditorPanel.Width = Math.Clamp(XamlRoot.Size.Width - 100, 240, 800);
        EditorScroll.MaxHeight = Math.Max(180, XamlRoot.Size.Height - 190);
        bool narrow = EditorPanel.Width < 500;
        PresetActions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        PointFields.ColumnDefinitions.Clear(); PointFields.RowDefinitions.Clear();
        for (int i = 0; i < (narrow ? 1 : 3); i++) PointFields.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (narrow ? 3 : 1); i++) PointFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < PointFields.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)PointFields.Children[i], narrow ? 0 : i);
            Grid.SetRow((FrameworkElement)PointFields.Children[i], narrow ? i : 0);
        }
    }
}
