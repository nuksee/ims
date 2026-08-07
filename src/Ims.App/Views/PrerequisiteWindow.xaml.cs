using System.Globalization;
using System.Text;
using System.Windows;
using Ims.Data.Informix;

namespace Ims.App.Views;

/// <summary>
/// Shown instead of the main window when the Client SDK cannot be used (PR-1.8).
/// </summary>
public partial class PrerequisiteWindow : Window
{
    private readonly CsdkDetectionResult _result;

    public PrerequisiteWindow(CsdkDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _result = result;
        InitializeComponent();

        MessageBlock.Text = result.Message
            ?? "The Informix Client SDK could not be used, and IMS has no further detail.";

        RemedyBlock.Text = result.Remedy
            ?? "Check the Client SDK installation.";

        DetailsBox.Text = BuildDetails(result);
    }

    /// <summary>
    /// The diagnostic block, in a form the user can paste into a message to whoever
    /// administers their workstation. NFR-11 in miniature.
    /// </summary>
    private static string BuildDetails(CsdkDetectionResult result)
    {
        // Invariant throughout: this block is a diagnostic to be pasted into a
        // message to whoever administers the workstation, not localisable prose.
        CultureInfo culture = CultureInfo.InvariantCulture;

        return new StringBuilder()
            .AppendLine(culture, $"Problem          : {result.Problem}")
            .AppendLine(culture, $"INFORMIXDIR      : {result.InformixDir ?? "(not set)"}")
            .AppendLine(culture, $"SDK version      : {result.Version ?? "(unknown)"}")
            .AppendLine(culture, $"ODBC driver name : {result.OdbcDriverName ?? "(none registered)"}")
            .AppendLine(culture, $"ODBC driver path : {result.OdbcDriverPath ?? "(none)"}")
            .AppendLine()
            .AppendLine("IMS requires the IBM Informix Client SDK and does not bundle it.")
            .ToString();
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildDetails(_result));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard. Not worth failing over.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
