using System.Globalization;
using System.Text;
using System.Windows;
using Ims.Core.Diagnostics;
using Ims.Data.Informix;

namespace Ims.App.Views;

/// <summary>
/// Says which build this is, in a form that can be pasted into a bug report.
/// </summary>
/// <remarks>
/// The version and commit are the point. A pilot user runs a folder that was
/// copied to them, and "0.1.0" is true of several builds — the commit is what
/// makes a report reproducible, so it is shown, selectable, and copyable in one
/// click rather than buried in a MessageBox nobody can select text from.
/// </remarks>
public partial class AboutWindow : Window
{
    private readonly CsdkDetectionResult _csdk;

    public AboutWindow(CsdkDetectionResult csdk)
    {
        ArgumentNullException.ThrowIfNull(csdk);

        _csdk = csdk;

        InitializeComponent();

        VersionLine.Text = $"Version {BuildInfo.Version}";
        VersionValue.Text = BuildInfo.Version;
        CommitValue.Text = BuildInfo.Commit;
        CsdkValue.Text = csdk.Version ?? "(unknown)";
        InformixDirValue.Text = csdk.InformixDir ?? "(not found)";
        OdbcDriverValue.Text = csdk.OdbcDriverName ?? "(none registered)";

        if (BuildInfo.IsModified)
        {
            ModifiedNotice.Visibility = Visibility.Visible;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        // Invariant throughout: this text is destined for a bug report, where a
        // locale-formatted version number helps nobody.
        CultureInfo c = CultureInfo.InvariantCulture;

        var details = new StringBuilder()
            .AppendLine("Informix Management Studio")
            .AppendLine(c, $"Version    : {BuildInfo.Version}")
            .AppendLine(c, $"Commit     : {BuildInfo.Commit}{(BuildInfo.IsModified ? " (modified)" : string.Empty)}")
            .AppendLine(c, $"Client SDK : {_csdk.Version ?? "(unknown)"}")
            .AppendLine(c, $"INFORMIXDIR: {_csdk.InformixDir ?? "(not found)"}")
            .AppendLine(c, $"ODBC driver: {_csdk.OdbcDriverName ?? "(none registered)"}")
            .AppendLine(c, $"OS         : {Environment.OSVersion.VersionString}")
            .AppendLine(c, $".NET       : {Environment.Version}");

        try
        {
            Clipboard.SetText(details.ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process can hold the clipboard open. Losing a copy is not
            // worth an error dialog on top of the one the user is already reading.
        }
    }
}
