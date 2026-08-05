using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ims.Core.Diagnostics;

/// <summary>
/// Wraps another logger provider and scrubs every message before it reaches it.
/// </summary>
/// <remarks>
/// <para>
/// PR-6.3 is a promise about what IMS never writes down. Making it a decorator
/// means the promise is kept by construction: a call site that interpolates a
/// password into a message still cannot get it into the log file, because the
/// only path to the file goes through here.
/// </para>
/// <para>
/// Register it around the real provider, not beside it — see
/// <c>LoggingBuilderExtensions.AddRedaction</c>.
/// </para>
/// </remarks>
public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
{
    private readonly ILoggerProvider _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RedactingLogger(_inner.CreateLogger(name)));

    public void Dispose()
    {
        _loggers.Clear();
        _inner.Dispose();
    }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // Format first, then scrub. Scrubbing the structured state instead would
            // leave the message template's interpolated values untouched.
            inner.Log(
                logLevel,
                eventId,
                state,
                exception,
                (s, e) => Redaction.Message(formatter(s, e)));
        }
    }
}

/// <summary>Registration helper for <see cref="RedactingLoggerProvider"/>.</summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Wraps every provider registered so far in redaction (PR-6.3).
    /// </summary>
    /// <remarks>
    /// Call this <em>after</em> adding the real providers. Anything registered
    /// afterwards bypasses redaction, which is why the app registers logging in
    /// exactly one place.
    /// </remarks>
    public static ILoggingBuilder AddRedaction(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existing = builder.Services
            .Where(d => d.ServiceType == typeof(ILoggerProvider))
            .ToList();

        foreach (var descriptor in existing)
        {
            builder.Services.Remove(descriptor);

            builder.Services.Add(new ServiceDescriptor(
                typeof(ILoggerProvider),
                provider =>
                {
                    var inner = (ILoggerProvider)(descriptor.ImplementationInstance
                        ?? descriptor.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

                    return new RedactingLoggerProvider(inner);
                },
                descriptor.Lifetime));
        }

        return builder;
    }
}
