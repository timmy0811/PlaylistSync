using Avalonia.Controls;

namespace PlaylistSync.App.Views;

/// <summary>A small confirm / error dialog. Closes with true for the confirm button.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow() => InitializeComponent();

    public MessageWindow(string title, string message, string confirmText, string? cancelText, bool destructive) : this()
    {
        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => Close(true);
        if (destructive)
        {
            ConfirmButton.Classes.Remove("accent");
            ConfirmButton.Classes.Add("danger");
        }

        if (cancelText is null)
        {
            CancelButton.IsVisible = false;
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
            CancelButton.Click += (_, _) => Close(false);
        }
        // Destructive actions default to Cancel so Enter doesn't delete by accident.
        Opened += (_, _) => (destructive && cancelText is not null ? CancelButton : ConfirmButton).Focus();
    }
}
