using System.Data.Odbc;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ims.Core.Connections;
using Ims.Core.Data;
using Ims.Core.Diagnostics;
using Ims.Core.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ims.Data.Informix;

/// <summary>
/// One connection to one Informix instance, over the CSDK's ODBC driver.
/// </summary>
/// <remarks>
/// <para>
/// The concrete side of the DEC-4 decision. Every server-touching method asserts it
/// is not on the UI thread before doing anything, so NFR-1 fails loudly at the call
/// site rather than quietly as the freeze RSK-3 warns about.
/// </para>
/// <para>
/// The session sends nothing of its own beyond what <see cref="OpenAsync"/> needs to
/// identify the instance, which is a documented consequence of connecting. Capability
/// probes are lazy and explicit rather than run at connect time, because PR-6.2 says
/// IMS sends no statement the user did not type or explicitly request.
/// </para>
/// </remarks>
public sealed class InformixOdbcSession : IInformixSession
{
    private readonly ICredentialResolver _credentials;
    private readonly string _driverName;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    private OdbcConnection? _connection;
    private OdbcTransaction? _transaction;
    private OdbcCommand? _runningCommand;
    private SessionState _state = SessionState.Closed;
    private bool _disposed;

    /// <summary>
    /// The streaming result currently holding this connection's cursor, if any.
    /// </summary>
    /// <remarks>
    /// A connection has one cursor, so a new statement cannot run while an old
    /// result is still open. The session closes it rather than waiting for the
    /// caller to — an earlier design gated on the caller disposing the result, and
    /// a caller that never did left the session permanently unusable. Ownership of
    /// a scarce resource belongs with whoever can guarantee its release.
    /// </remarks>
    private IStatementResult? _openResult;

    public InformixOdbcSession(
        ConnectionDescriptor descriptor,
        ICredentialResolver credentials,
        string driverName,
        ILogger<InformixOdbcSession>? logger = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _driverName = driverName ?? throw new ArgumentNullException(nameof(driverName));
        _logger = logger ?? NullLogger<InformixOdbcSession>.Instance;
    }

    public ConnectionDescriptor Descriptor { get; }

    public SessionState State => _state;

    public TransactionState TransactionState { get; private set; } = TransactionState.AutoCommit;

    public InformixServerInfo? ServerInfo { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ServerCallGuard.AssertNotOnUiThread("Open an Informix connection");

        SetState(SessionState.Connecting);

        string? password = await _credentials
            .GetPasswordAsync(Descriptor, cancellationToken)
            .ConfigureAwait(false);

        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor,
            _driverName,
            password);

        var connection = new OdbcConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OdbcException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            InformixError error = InformixErrorTranslator.Translate(ex);

            // Never the connection string, which still holds the password (PR-6.3).
            _logger.LogWarning(
                "Connection to {Target} failed: {Error}",
                Descriptor.TargetLabel,
                error.ToString());

            SetState(SessionState.Closed, error);
            throw new InformixException(error, ex);
        }
        finally
        {
            // Not kept beyond the moment of use (DEC-9). The connection string that
            // embeds it is local to this method and goes out of scope with it.
            password = null;
        }

        _connection = connection;

        // A dropped connection must be noticed and said clearly (PR-1.7).
        _connection.StateChange += OnUnderlyingStateChange;

        SetState(SessionState.Open);

        ServerInfo = await ReadServerInfoAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Connected to {Target} ({Version}).",
            Descriptor.TargetLabel,
            ServerInfo?.VersionBanner ?? "version unknown");
    }

    public async IAsyncEnumerable<StatementOutcome> ExecuteScriptAsync(
        string script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ServerCallGuard.AssertNotOnUiThread("Execute a script");

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(script);

        for (int index = 0; index < statements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqlStatement statement = statements[index];

            // Only the final statement may stream. A connection holds one cursor at
            // a time, so an earlier SELECT has to be read and closed before the next
            // statement can execute at all — see BufferedStatementResult.
            bool isLast = index == statements.Count - 1;

            StatementOutcome outcome = await ExecuteOneAsync(
                statement.Text,
                index,
                statement.Offset,
                cancellationToken,
                bufferResult: !isLast).ConfigureAwait(false);

            yield return outcome;

            // PR-3.4 shows each result in sequence; stopping at the first failure is
            // the caller's decision, not the session's, so the loop continues only
            // while the caller keeps enumerating.
        }
    }

    public Task<StatementOutcome> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ServerCallGuard.AssertNotOnUiThread("Execute a statement");

        return ExecuteOneAsync(sql, index: 0, scriptOffset: 0, cancellationToken);
    }

    /// <summary>
    /// PR-3.5: stop the running statement without terminating the session or the
    /// application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether the CSDK's ODBC driver honours this, and leaves the session usable
    /// afterwards, is exactly what the smoke test's cancellation probe measures. If
    /// it does not, the fallback is a second connection issuing an administrative
    /// cancel — which would be the first thing IMS does that the user did not type,
    /// and so needs weighing against PR-6.2.
    /// </para>
    /// <para>
    /// <strong>Measured 2026-08-06 against 14.10: it does not.</strong> Two
    /// statements, one slow by sorting and one slow by scanning, both ran on to their
    /// 30s command timeout roughly 30 seconds after <c>Cancel()</c> was called. The
    /// session was usable immediately afterwards in both cases, so the second half of
    /// PR-3.5 holds and only the first half fails. Sorting is not the cause; the call
    /// simply does not reach the server.
    /// </para>
    /// <para>
    /// So this method currently returns without stopping anything, and the UI's cancel
    /// gesture is misleading rather than merely ineffective — it returns control while
    /// the statement runs on. Before adopting the second-connection fallback, try
    /// <c>SQL_ATTR_ASYNC_ENABLE</c>: System.Data.Odbc executes synchronously, and
    /// <c>SQLCancel</c> against a synchronous handle is documented to take effect only
    /// in limited states. That is a spike, not a redesign, and it is the cheaper
    /// explanation.
    /// </para>
    /// </remarks>
    public Task CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OdbcCommand? running = _runningCommand;

        if (running is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Cancelling the running statement on {Target}.", Descriptor.TargetLabel);

        try
        {
            running.Cancel();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning("Cancel failed: {Message}", ex.Message);
        }
        catch (InvalidOperationException)
        {
            // The statement finished between the null check and the cancel.
        }

        return Task.CompletedTask;
    }

    /// <summary>Begins an explicit transaction, taking the session off autocommit (PR-3.7).</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Begin a transaction");
        OdbcConnection connection = RequireOpenConnection();

        if (_transaction is not null)
        {
            return;
        }

        _transaction = await Task.Run(connection.BeginTransaction, cancellationToken)
            .ConfigureAwait(false);

        TransactionState = TransactionState.Open;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Commit");

        if (_transaction is null)
        {
            return;
        }

        OdbcTransaction transaction = _transaction;
        _transaction = null;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);

        TransactionState = TransactionState.AutoCommit;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Roll back");

        if (_transaction is null)
        {
            return;
        }

        OdbcTransaction transaction = _transaction;
        _transaction = null;

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);

        TransactionState = TransactionState.AutoCommit;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_openResult is { } open)
        {
            _openResult = null;
            await open.DisposeAsync().ConfigureAwait(false);
        }

        if (_transaction is not null)
        {
            // An open transaction at dispose is rolled back, never committed by
            // accident. PR-3.7 wants commit to be an explicit act.
            try
            {
                await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OdbcException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_connection is not null)
        {
            _connection.StateChange -= OnUnderlyingStateChange;
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _executionGate.Dispose();
        SetState(SessionState.Closed);
    }

    private async Task<StatementOutcome> ExecuteOneAsync(
        string sql,
        int index,
        int scriptOffset,
        CancellationToken cancellationToken,
        bool bufferResult = false)
    {
        OdbcConnection connection = RequireOpenConnection();

        // One statement at a time per session, as Informix itself expects.
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Close whatever still holds the cursor. Without this, the second execute on
        // a session blocks forever behind a result the caller has not disposed.
        if (_openResult is { } previous)
        {
            _openResult = null;
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var command = new OdbcCommand(sql, connection, _transaction) { CommandTimeout = 0 };
        var cancelledByUser = false;

        // Cancelling the token must reach the server, not just abandon the await.
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            cancelledByUser = true;
            try
            {
                command.Cancel();
            }
            catch (OdbcException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });

        _runningCommand = command;
        SetState(SessionState.Executing);

        // A streaming row set keeps the command and reader alive past this method;
        // everything else is finished with them here.
        var resultOwnsCommand = false;

        try
        {
            if (LooksLikeRowSet(sql))
            {
                OdbcDataReader reader = (OdbcDataReader)await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                IStatementResult streaming = OdbcStatementResult.Create(command, reader);

                if (bufferResult)
                {
                    // Not the last statement in the script. The cursor has to be
                    // released before the next statement can execute at all, so the
                    // rows are read now, up to a cap, and the reader closed.
                    BufferedStatementResult buffered = await BufferedStatementResult
                        .CreateAsync(streaming, cancellationToken)
                        .ConfigureAwait(false);

                    if (buffered.WasTruncated)
                    {
                        _logger.LogInformation(
                            "Statement {Index} returned more than {Cap} rows; the result was "
                            + "truncated so the rest of the script could run.",
                            index,
                            BufferedStatementResult.MaximumBufferedRows);
                    }

                    return new StatementOutcome
                    {
                        Index = index,
                        Sql = sql,
                        ScriptOffset = scriptOffset,
                        Kind = StatementResultKind.RowSet,
                        Result = buffered,
                        Elapsed = stopwatch.Elapsed,
                        TransactionState = TransactionState,
                    };
                }

                resultOwnsCommand = true;

                // The session, not the caller, owns the open cursor. Clearing the
                // field on disposal keeps the two in step without depending on the
                // caller to dispose at all.
                var tracked = new TrackedStatementResult(streaming, () => _openResult = null);
                _openResult = tracked;

                return new StatementOutcome
                {
                    Index = index,
                    Sql = sql,
                    ScriptOffset = scriptOffset,
                    Kind = StatementResultKind.RowSet,
                    Result = tracked,
                    Elapsed = stopwatch.Elapsed,
                    TransactionState = TransactionState,
                };
            }

            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (_transaction is not null)
            {
                TransactionState = TransactionState.Uncommitted;
            }

            return new StatementOutcome
            {
                Index = index,
                Sql = sql,
                ScriptOffset = scriptOffset,
                Kind = affected >= 0 ? StatementResultKind.RowsAffected : StatementResultKind.NoResult,
                RowsAffected = affected >= 0 ? affected : null,
                Elapsed = stopwatch.Elapsed,
                TransactionState = TransactionState,
            };
        }
        catch (OdbcException ex)
        {
            stopwatch.Stop();

            InformixError error = InformixErrorTranslator.Translate(
                ex, index, scriptOffset, cancelledByUser);

            if (error.IsConnectionLost)
            {
                SetState(SessionState.Broken, error);
            }
            else if (_transaction is not null)
            {
                TransactionState = TransactionState.Failed;
            }

            // PR-6.3: the statement is redacted, because a literal can be real data.
            _logger.LogWarning(
                "Statement {Index} failed on {Target}: {Error} [{Sql}]",
                index,
                Descriptor.TargetLabel,
                error.ToString(),
                Redaction.Sql(sql));

            return new StatementOutcome
            {
                Index = index,
                Sql = sql,
                ScriptOffset = scriptOffset,
                Kind = StatementResultKind.Failed,
                Error = error,
                Elapsed = stopwatch.Elapsed,
                TransactionState = TransactionState,
            };
        }
        finally
        {
            _runningCommand = null;

            if (_state == SessionState.Executing)
            {
                SetState(SessionState.Open);
            }

            if (!resultOwnsCommand)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }

            // Always released here. The previous design released it when the caller
            // disposed the result, so a caller that never did wedged the session
            // permanently — which is exactly what happened on the first real use.
            ReleaseAfterResult();
        }
    }

    private void ReleaseAfterResult()
    {
        try
        {
            _executionGate.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Whether to expect rows back.
    /// </summary>
    /// <remarks>
    /// A keyword test rather than a parse. Getting it wrong is cheap — the driver
    /// reports the truth either way — and a full Informix grammar is far beyond what
    /// PR-3.3 needs.
    /// </remarks>
    private static bool LooksLikeRowSet(string sql) =>
        SqlText.LeadingKeyword(SqlText.StripLiteralsAndComments(sql))
            is "SELECT" or "EXECUTE" or "WITH";

    /// <summary>
    /// Reads the version banner. The one statement IMS issues on its own behalf.
    /// </summary>
    /// <remarks>
    /// Justified under PR-6.2 as a documented consequence of connecting: NFR-4 needs
    /// it to degrade gracefully on any server version reached, and PR-5.6 shows it. Every
    /// other capability is probed lazily, when a feature actually needs it, rather
    /// than speculatively at connect time.
    /// </remarks>
    private async Task<InformixServerInfo?> ReadServerInfoAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT FIRST 1 DBINFO('version', 'full') FROM systables";

        try
        {
            using var command = new OdbcCommand(sql, _connection) { CommandTimeout = 15 };

            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            string banner = value?.ToString()?.Trim() ?? string.Empty;

            return new InformixServerInfo
            {
                VersionBanner = banner,
                Version = ParseVersion(banner),
                ServerName = Descriptor.ServerName,

                // Deliberately empty. NFR-4 asks for capability detection rather than
                // version branching, and a capability nobody has asked about yet is
                // one IMS has no business probing for (PR-6.2).
                Capabilities = new HashSet<InformixCapability>(),
            };
        }
        catch (OdbcException ex)
        {
            // Not fatal: NFR-4 says degrade gracefully rather than fail opaquely.
            _logger.LogWarning("Could not read the server version: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Pulls a version out of an Informix banner such as
    /// "IBM Informix Dynamic Server Version 14.10.FC9W1X2".
    /// </summary>
    internal static Version ParseVersion(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner))
        {
            return new Version(0, 0);
        }

        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(
                banner,
                @"(\d+)\.(\d+)",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(1));

        return match.Success
               && int.TryParse(match.Groups[1].Value, out int major)
               && int.TryParse(match.Groups[2].Value, out int minor)
            ? new Version(major, minor)
            : new Version(0, 0);
    }

    private OdbcConnection RequireOpenConnection() =>
        _connection ?? throw new InvalidOperationException(
            "The session is not open. Call OpenAsync first.");

    private void OnUnderlyingStateChange(object sender, System.Data.StateChangeEventArgs e)
    {
        if (e.CurrentState is System.Data.ConnectionState.Closed or System.Data.ConnectionState.Broken
            && _state is not SessionState.Closed)
        {
            SetState(
                SessionState.Broken,
                new InformixError
                {
                    SqlCode = 0,
                    ServerMessage = "The connection to the server was lost.",
                    Explanation = "IMS can reconnect without losing your editor contents.",
                    IsConnectionLost = true,
                });
        }
    }

    private void SetState(SessionState next, InformixError? error = null)
    {
        SessionState previous = _state;

        if (previous == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(previous, next, error));
    }
}

/// <summary>
/// Wraps a streaming result so the session knows when its cursor is released.
/// </summary>
internal sealed class TrackedStatementResult(IStatementResult inner, Action onDisposed)
    : IStatementResult
{
    private bool _disposed;

    public IReadOnlyList<ResultColumn> Columns => inner.Columns;

    public long RowsRead => inner.RowsRead;

    public bool IsComplete => inner.IsComplete;

    public bool WasTruncated => inner.WasTruncated;

    public IAsyncEnumerable<InformixValue[]> ReadRowsAsync(CancellationToken cancellationToken) =>
        inner.ReadRowsAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await inner.DisposeAsync().ConfigureAwait(false);
        onDisposed();
    }
}

