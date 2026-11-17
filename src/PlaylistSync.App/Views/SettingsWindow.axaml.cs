using Avalonia.Controls;
using PlaylistSync.App.ViewModels;

namespace PlaylistSync.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel? _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (_vm is { IsGlobal: true }) _vm.CheckToolsCommand.Execute(null);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CloseRequested -= OnCloseRequested;
        _vm = DataContext as SettingsViewModel;
        if (_vm is not null) _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool saved) => Close(saved);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The title bar's close button cancels, like the Cancel button (stops a running tool check).
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
            if (!e.Cancel) _vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
