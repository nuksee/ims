using System.Windows;
using System.Windows.Controls;
using Ims.Core.Connections;
using Ims.Data.Informix;
using Ims.Data.Informix.Security;

namespace Ims.App.Views;

/// <summary>
/// Creates or edits a saved connection (PR-1.1 to PR-1.5).
/// </summary>
public partial class ConnectionDialog : Window
{
    private readonly WindowsCredentialStore _credentials;
    private readonly Guid _id;
    private readonly ConnectionDescriptor? _existing;

    public ConnectionDialog(ConnectionDescriptor? existing, WindowsCredentialStore credentials)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _id = existing?.Id ?? Guid.NewGuid();
        _existing = existing;

        InitializeComponent();

        ClientLocaleBox.ItemsSource = InformixLocale.CommonLocales;
        DatabaseLocaleBox.ItemsSource = InformixLocale.CommonLocales;

        EnvironmentBox.ItemsSource = new[]
        {
            InformixEnvironment.Development,
            InformixEnvironment.Uat,
            InformixEnvironment.Production,
            InformixEnvironment.Unspecified,
        };

        AuthModeBox.ItemsSource = new[]
        {
            InformixAuthenticationMode.Local,
            InformixAuthenticationMode.LdapPam,
        };

        if (existing is null)
        {
            Title = "New connection";
            EnvironmentBox.SelectedItem = InformixEnvironment.Development;
            AuthModeBox.SelectedItem = InformixAuthenticationMode.Local;
            return;
        }

        Title = "Edit connection";
        DisplayNameBox.Text = existing.DisplayName;
        ServerNameBox.Text = existing.ServerName;
        HostBox.Text = existing.Host;
        ServiceBox.Text = existing.Service;
        ProtocolBox.Text = existing.Protocol;
        DatabaseBox.Text = existing.Database ?? string.Empty;
        UserNameBox.Text = existing.UserName ?? string.Empty;
        GroupBox2.Text = existing.Group ?? string.Empty;
        EnvironmentBox.SelectedItem = existing.Environment;
        AuthModeBox.SelectedItem = existing.AuthenticationMode;
        ClientLocaleBox.Text = existing.ClientLocale ?? string.Empty;
        DatabaseLocaleBox.Text = existing.DatabaseLocale ?? string.Empty;
    }

    /// <summary>The saved connection, once the dialog returns true.</summary>
    public ConnectionDescriptor? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate(out string? problem))
        {
            MessageBox.Show(this, problem, "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Start from the existing descriptor so that settings this dialog does not
        // show (the connect timeout, for one) survive an edit rather than quietly
        // resetting to their defaults.
        ConnectionDescriptor template = _existing ?? new ConnectionDescriptor
        {
            Id = _id,
            DisplayName = string.Empty,
            ServerName = string.Empty,
            Host = string.Empty,
            Service = string.Empty,
        };

        Result = template with
        {
            Id = _id,
            DisplayName = DisplayNameBox.Text.Trim(),
            ServerName = ServerNameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Service = ServiceBox.Text.Trim(),
            Protocol = string.IsNullOrWhiteSpace(ProtocolBox.Text) ? "onsoctcp" : ProtocolBox.Text.Trim(),
            Database = Blank(DatabaseBox.Text),
            UserName = Blank(UserNameBox.Text),
            Group = Blank(GroupBox2.Text),
            Environment = (InformixEnvironment)(EnvironmentBox.SelectedItem
                                                ?? InformixEnvironment.Unspecified),
            AuthenticationMode = (InformixAuthenticationMode)(AuthModeBox.SelectedItem
                                                              ?? InformixAuthenticationMode.Local),
            ClientLocale = Blank(ClientLocaleBox.Text),
            DatabaseLocale = Blank(DatabaseLocaleBox.Text),
        };

        // DEC-9: the password goes to Windows Credential Manager and nowhere else.
        // An empty box leaves whatever was already stored alone, so editing a
        // connection does not silently discard its credential.
        if (PasswordBox.Password.Length > 0)
        {
            _credentials.Save(Result, Result.UserName ?? string.Empty, PasswordBox.Password);
            PasswordBox.Clear();
        }

        DialogResult = true;
    }

    /// <summary>
    /// Fills the server fields from this machine's own connectivity configuration.
    /// </summary>
    /// <remarks>
    /// Groundwork for PR-1.9. Both sources Windows uses are offered, labelled, and
    /// not de-duplicated — a discrepancy between the registry and the file is
    /// something the user needs to see rather than something IMS should hide.
    /// </remarks>
    private void OnImportSqlHosts(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<SqlHostsEntry> entries = SqlHostsReader.ReadAll();

        if (entries.Count == 0)
        {
            MessageBox.Show(
                this,
                "No sqlhosts entries were found, in either the registry or "
                + "%INFORMIXDIR%\\etc\\sqlhosts.",
                "Nothing to import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var picker = new SqlHostsPicker(entries) { Owner = this };

        if (picker.ShowDialog() != true || picker.Selected is not { } entry)
        {
            return;
        }

        ServerNameBox.Text = entry.ServerName;
        HostBox.Text = entry.Host;
        ServiceBox.Text = entry.Service;
        ProtocolBox.Text = entry.Protocol;

        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text))
        {
            DisplayNameBox.Text = entry.ServerName;
        }
    }

    private bool Validate(out string? problem)
    {
        foreach ((TextBox box, string label) in ((TextBox, string)[])
                 [
                     (DisplayNameBox, "Display name"),
                     (ServerNameBox, "Server name"),
                     (HostBox, "Host"),
                     (ServiceBox, "Service / port"),
                 ])
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                problem = $"{label} is required.";
                box.Focus();
                return false;
            }
        }

        foreach ((ComboBox box, string label) in ((ComboBox, string)[])
                 [
                     (ClientLocaleBox, "Client locale"),
                     (DatabaseLocaleBox, "DB locale"),
                 ])
        {
            if (!string.IsNullOrWhiteSpace(box.Text) && !InformixLocale.IsWellFormed(box.Text.Trim()))
            {
                problem = $"{label} '{box.Text.Trim()}' is not a locale name. "
                          + "Use language_territory.codeset, for example en_US.819 or en_US.utf8, "
                          + "or leave it blank for the client default.";
                box.Focus();
                return false;
            }
        }

        problem = null;
        return true;
    }

    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
