using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView;

public sealed partial class UserAgreementDialog : ContentDialog
{
    public UserAgreementViewModel ViewModel { get; }
    public UserAgreementDialog(UserAgreementViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_Changed;
        Closed += (_, _) => ViewModel.PropertyChanged -= ViewModel_Changed;
    }

    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Document)) DocumentScroll.ChangeView(null, 0, null, true);
    }

    private async void Accept_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        try { args.Cancel = !await ViewModel.AcceptAsync(); }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (ViewModel.IsSaving) args.Cancel = true;
    }
}
