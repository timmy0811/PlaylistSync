using Avalonia.Controls;
using PlaylistSync.App.ViewModels;

namespace PlaylistSync.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.InitializeAsync();
        };
    }
}
