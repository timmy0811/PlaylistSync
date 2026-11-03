using CommunityToolkit.Mvvm.ComponentModel;

namespace PlaylistSync.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Ready";
}
