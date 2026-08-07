using System.Windows;
using Ims.Core.Connections;

namespace Ims.App.Views;

/// <summary>
/// Asks for a password when Credential Manager has none stored (DEC-9).
/// </summary>
public partial class PasswordPrompt : Window
{
    public PasswordPrompt(ConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        InitializeComponent();

        PromptText.Text = descriptor.UserName is { Length: > 0 } user
            ? $"Enter the password for {user} on {descriptor.TargetLabel}."
            : $"Enter the password for {descriptor.TargetLabel}.";
    }

    public string? Password { get; private set; }

    /// <summary>True when the user asked for the password to be stored.</summary>
    public bool Remember { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Password = PasswordEntry.Password;
        Remember = RememberBox.IsChecked == true;

        PasswordEntry.Clear();
        DialogResult = true;
    }
}
