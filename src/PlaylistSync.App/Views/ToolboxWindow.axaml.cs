using Avalonia.Controls;
using Avalonia.Input;

namespace PlaylistSync.App.Views;

/// <summary>Non-modal; closing it does not cancel a running download (the view model lives on).</summary>
public partial class ToolboxWindow : Window
{
    public ToolboxWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
