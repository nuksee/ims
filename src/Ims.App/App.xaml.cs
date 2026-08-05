using System.Windows;
using System.Windows.Threading;
using Ims.Core.Diagnostics;

namespace Ims.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // NFR-1: install the detector that lets ServerCallGuard fail loudly if any
        // provider round trip is ever attempted on the dispatcher thread.
        Dispatcher dispatcher = Dispatcher;
        ServerCallGuard.ConfigureUiThreadDetector(() => dispatcher.CheckAccess());
    }
}
