using System.Windows;
using System.Windows.Threading;
using Ims.Core.Diagnostics;
using Ims.Data.Informix;
using Microsoft.Extensions.Logging;

namespace Ims.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;

    /// <summary>
    /// The Client SDK detection result, resolved once at startup and shared by the
    /// windows that report it.
    /// </summary>
    public CsdkDetectionResult? Csdk { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // NFR-1. Ims.Core has no Windows dependency, so the host supplies the answer
        // to "am I on the UI thread?". With this installed, any provider round trip
        // attempted on the dispatcher throws at the call site instead of freezing IMS.
        Dispatcher dispatcher = Dispatcher;
        ServerCallGuard.ConfigureUiThreadDetector(dispatcher.CheckAccess);

        _loggerFactory = CreateLoggerFactory();
        _logger = _loggerFactory.CreateLogger<App>();

        // NFR-3: a crash must be recorded, and PR-3.9 will need this hook when the
        // editor's crash-safe autosave lands in Slice 1.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // PR-1.8: a missing or misconfigured SDK is reported at startup as its own
        // condition, not later as a mysterious connection failure. This is registry
        // and file-system inspection only — it opens no connection.
        Csdk = CsdkLocator.Detect();

        if (Csdk.IsUsable)
        {
            _logger.LogInformation(
                "Informix Client SDK {Version} at {Directory}, ODBC driver '{Driver}'.",
                Csdk.Version ?? "(unknown version)",
                Csdk.InformixDir,
                Csdk.OdbcDriverName);

            MainWindow = new MainWindow(Csdk);
        }
        else
        {
            _logger.LogError(
                "Informix Client SDK unusable ({Problem}): {Message}",
                Csdk.Problem,
                Csdk.Message);

            MainWindow = new Views.PrerequisiteWindow(Csdk);
        }

        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Builds the logging stack. One place, deliberately: <c>AddRedaction</c> wraps
    /// the providers registered before it, so anything added elsewhere would bypass
    /// PR-6.3.
    /// </summary>
    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));
            builder.AddRedaction();
        });

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception on the dispatcher thread.");

        MessageBox.Show(
            $"IMS hit an unexpected error.\n\n{e.Exception.Message}\n\n"
            + $"Details were written to {FileLoggerProvider.DefaultDirectory}.",
            "Informix Management Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: NFR-3 wants no unhandled termination across a working day,
        // and losing an editor's contents to a non-fatal fault would be worse.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread.");
}
