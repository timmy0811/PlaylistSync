using Avalonia.Controls;
using PlaylistSync.App.ViewModels;

namespace PlaylistSync.App.Views;

public partial class PlaylistEditorWindow : Window
{
    private PlaylistEditorViewModel? _vm;

    public PlaylistEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => UrlBox.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CloseRequested -= OnCloseRequested;
        _vm = DataContext as PlaylistEditorViewModel;
        if (_vm is not null) _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool saved) => Close(saved);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing via the title bar cancels pending checks like the Cancel button does.
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
            if (!e.Cancel) _vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
