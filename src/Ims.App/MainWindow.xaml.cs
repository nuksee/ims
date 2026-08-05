using System.Windows;
using Ims.Data.Informix;

namespace Ims.App;

public partial class MainWindow : Window
{
    private readonly CsdkDetectionResult _csdk;

    public MainWindow(CsdkDetectionResult csdk)
    {
        ArgumentNullException.ThrowIfNull(csdk);

        _csdk = csdk;
        InitializeComponent();

        // PR-8.3: name the thing IMS is using, so the user leaves more capable at the
        // command line rather than less.
        CsdkStatusText.Text = $"Client SDK {csdk.Version ?? "(unknown)"} — {csdk.OdbcDriverName}";
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "Informix Management Studio\n\n"
            + $"Client SDK : {_csdk.Version ?? "(unknown)"}\n"
            + $"INFORMIXDIR: {_csdk.InformixDir}\n"
            + $"ODBC driver: {_csdk.OdbcDriverName}\n\n"
            + "IMS sends no statement you did not type, and emits no telemetry.",
            "About IMS",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
}
