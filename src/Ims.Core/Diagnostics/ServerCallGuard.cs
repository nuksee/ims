using System.Diagnostics.CodeAnalysis;

namespace Ims.Core.Diagnostics;

/// <summary>
/// Fails loudly if server or network work is attempted on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// NFR-1 says "the UI never blocks on server or network work" and adds, unusually
/// for a non-functional requirement, "this is a functional requirement, not
/// polish". PR-8.5 agrees: the tool competes with a terminal, and perceived
/// slowness is a defect. RSK-3 is that IMS is not actually better than
/// <c>dbaccess</c> and so goes unused.
/// </para>
/// <para>
/// Conventions do not survive a part-time solo build (DEC-11), so this is a
/// mechanism instead. <c>Ims.App</c> installs a detector at startup; the provider
/// layer calls <see cref="AssertNotOnUiThread"/> before every round trip. In debug
/// builds a violation throws at the call site, which is the only time it is cheap
/// to fix.
/// </para>
/// </remarks>
public static class ServerCallGuard
{
    private static Func<bool>? _isUiThread;

    /// <summary>
    /// Installs the UI-thread detector. Called once, by the application host.
    /// </summary>
    /// <remarks>
    /// Ims.Core has no Windows dependency (NFR-5), so it cannot ask a
    /// <c>Dispatcher</c> anything — the host supplies the answer instead.
    /// </remarks>
    public static void ConfigureUiThreadDetector(Func<bool> detector) =>
        _isUiThread = detector ?? throw new ArgumentNullException(nameof(detector));

    /// <summary>Removes the detector. Used by tests to isolate.</summary>
    public static void ResetForTesting() => _isUiThread = null;

    /// <summary>True when a detector is installed and reports the UI thread.</summary>
    public static bool IsOnUiThread => _isUiThread?.Invoke() ?? false;

    /// <summary>
    /// Throws if called on the UI thread. No-op when no detector is installed,
    /// which is the case in tests and in the smoke-test console.
    /// </summary>
    /// <exception cref="UiThreadBlockedException">The call would block the UI.</exception>
    public static void AssertNotOnUiThread(
        string operation,
        [SuppressMessage("Usage", "CA1801", Justification = "Kept for call-site clarity.")]
        bool throwInRelease = true)
    {
        if (!IsOnUiThread)
        {
            return;
        }

        var exception = new UiThreadBlockedException(operation);

#if DEBUG
        throw exception;
#else
        if (throwInRelease)
        {
            throw exception;
        }
#endif
    }
}

/// <summary>
/// Thrown when a server round trip is attempted on the UI thread (NFR-1).
/// </summary>
public sealed class UiThreadBlockedException(string operation)
    : InvalidOperationException(
        $"'{operation}' would block the UI thread. Server and network work must run off the "
        + "dispatcher — NFR-1 makes this a functional requirement, not a style preference.")
{
    public string Operation { get; } = operation;
}
